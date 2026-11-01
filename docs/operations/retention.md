# Retenção operacional (#46)

Políticas aprovadas pelo responsável em 21/10/2026, registradas nas issues #46, #68 e #9.
O worker é o único executor. Não há endpoint público de expurgo ou revisão.

## Ativação e rollback

1. Aplicar a migration aditiva `20261021150000_OperationalRetention` pelo processo de release em banco validado.
2. Atualizar web e workers da mesma release. O consumidor de participação antigo não conhece o isolamento
   por tipo: **parar todos os workers antigos antes de habilitar retenção**. Não executar versões mistas com retenção ligada.
3. Habilitar `Retention__Enabled=true` no worker somente após validação da release e do backup recuperável.
   O padrão é desabilitado: o merge não executa expurgo nem autoriza deploy em produção.
4. Para interromper, desabilitar a opção e reiniciar os workers; uma transação cancelada volta integralmente.
   Para rollback de binário, manter o schema e suspender a linha `retention` antes de iniciar workers antigos:
   operador autorizado move a linha para `Failed`, com `completed_at`, `lease_expires_at=null` e causa
   `RollbackPaused`, depois de parar o worker. Registrar a ação no procedimento de release. Não remover a linha
   recorrente nem aplicar Down. Workers antigos só podem retornar quando não houver job de retenção reivindicável.
   Exclusões já confirmadas não são reversíveis pelo binário: recuperação exige procedimento de backup aprovado.

## Revisar falha de entrega

Investigue a falha individual e o efeito de negócio antes de registrar a decisão. Não considere payload vazio
como revisão. Acesso ao terminal segue a mesma fronteira operacional do bootstrap administrativo: host
restrito, configuração local e operador autorizado. O rótulo `--operator` declara o responsável; não é autenticação
por si só. Nunca publique payloads, destinatários ou credenciais em logs/issues.

```sh
dotnet Duora.Web.dll retention review-delivery --id <uuid-da-entrega> --operator <operador> --resolution resolved
dotnet Duora.Web.dll retention review-delivery --id <uuid-da-entrega> --operator <operador> --resolution accepted-loss
```

Use `resolved` após resolver a causa/efeito; `accepted-loss` somente após aceitar explicitamente a perda daquela
entrega. A operação só aceita `Failed` com conclusão registrada e sem revisão anterior. Grava data e auditoria
na mesma transação; não altera status para Delivered, não reenvia e não reinicia a contagem de 30 dias.
Código de saída 0 indica alteração, 1 indica estado incompatível/já revisado e 2 argumentos inválidos.

Para o consumidor `selfie-attempts-suspension`, seguir antes a
[recuperação manual do ADR-0043](../architecture/adr/0043-limite-de-reprovacoes-da-selfie.md#recuperação-manual-de-entrega-morta-306):
o payload terminal pode estar vazio, mas a conta pode ser localizada pelo `resource_id` do evento e pelo ledger.
Registrar a revisão não efetiva a suspensão. Conferir o efeito e as limitações da ferramenta de moderação antes
de marcar `resolved`; o endpoint HTTP existente aceita apenas `TermsViolation`, não o motivo da selfie.

## Falha do job

O lease dura 60 segundos, o lote tem teto real de 3 segundos, statement timeout de 2 segundos e espera por
lock de 500 ms. O job usa fencing, lock e conclusão/reagendamento na transação das exclusões. Não há chamada
externa durante o lease; heartbeat não é necessário para esse lote interno limitado. Após oito tentativas,
fica `Failed` e gera sinal para intervenção. Corrija a causa antes de retomar:

```sh
dotnet Duora.Web.dll retention resume --operator <operador>
```

A retomada só aceita job Failed, zera tentativas e preserva cutoff/identidade, com auditoria. Backlog continua
em novos lotes; ao esgotar, programa 24 horas adiante. Não cria uma linha para cada dia perdido.

## Evidência e monitoramento

O meter `Duora.Retention` preserva contagem total de linhas (`duora.retention.rows`), exclusões por tabela,
duração do lote, falhas de entrega sem revisão acima de 30 dias e jobs de retenção falhos. O worker consulta
os totais uma vez por minuto e emite o aviso agregado existente quando há revisão pendente. Conecte esse meter
ao coletor usado no ambiente; os instrumentos não comprovam que um backend externo de métricas esteja configurado.

### Atraso de expurgo e atraso de consumidor (#306, item 2)

`duora.retention.oldest_seconds{table}` passa a medir **segundos além do prazo de retenção do item elegível
mais atrasado**, com zero quando não há item elegível. Antes media a idade bruta do registro mais antigo,
inclusive pedidos de suspensão legitimamente em espera. A mudança de semântica vale para as quatro tabelas;
comparações históricas precisam considerar a versão da aplicação. `duora.retention.rows` continua sendo inventário
total, incluindo linhas ainda inelegíveis.

| Tabela | Elegibilidade e início do atraso |
|---|---|
| `outbox_events` | Sem nenhuma entrega referenciando o evento; `occurred_at + 7 dias` |
| `outbox_deliveries` | `Delivered`/`Expired`: `completed_at + 7 dias`; `Failed` com revisão explícita: `completed_at + 30 dias` |
| `webhook_receipts` | `Processed`/`Rejected`: `processed_at + 30 dias` |
| `jobs` | `Done`, exceto o job recorrente `retention`: `completed_at + 7 dias` |

As comparações de expurgo continuam estritas (`<`), como na limpeza. Datas de conclusão ausentes não são
elegíveis. Um evento com entregas terminais só entra no cálculo depois que elas forem efetivamente removidas;
a observação não simula exclusões futuras. É um snapshot de elegibilidade por estado/data, sem tomar locks:
linhas temporariamente ocupadas podem ser puladas pelo executor. Usa o relógio da aplicação e o instante atual,
enquanto o executor mantém seu cutoff durante a recuperação de backlog. A cadência diária e lotes limitados
podem produzir atraso positivo esperado; não há SLO demonstrado para escolher um limiar de alerta.

Para não esconder entregas que impedem o expurgo, o mesmo meter publica três gauges por `consumer,state`:

- `duora.outbox.rows`: quantidade de entregas não concluídas com sucesso, incluindo falhas terminais.
- `duora.outbox.oldest_seconds`: idade desde `occurred_at` do evento mais antigo naquela classe; é inventário,
  não atraso. Em particular, mostra a idade dos pedidos que continuam aguardando outra medida.
- `duora.outbox.processing_lag_seconds`: maior tempo desde a elegibilidade ao próximo claim: `available_at`
  para `ready` e `lease_expires_at` para `lease_expired`. Zero nas outras classes. Um claim/reagendamento reinicia
  esta espera; o sinal não mede tempo total até conclusão nem substitui a idade/volume de retries e falhas.

| `state` | Critério |
|---|---|
| `awaiting_other_measure` | `Pending`, consumidor `selfie-attempts-suspension`, `last_error=AwaitingOtherMeasure` e `available_at` ainda no futuro |
| `ready` | Todo `Pending` com `available_at <= agora`, inclusive espera/retry vencido ou deadline de negócio vencido |
| `scheduled` | Demais `Pending` futuros, incluindo backoff por falha e contenção `Busy` |
| `in_flight` | `InFlight` com lease ainda válido |
| `lease_expired` | `InFlight` com lease vencido, inclusive no instante exato do vencimento |
| `failed` | Todo `Failed`, com ou sem revisão; segue visível até seu expurgo autorizado |

`Delivered` e `Expired` continuam no inventário/atraso de retenção, mas não são trabalho de consumidor pendente.
Os consumidores conhecidos são `transactional-email`, `media-processing`, `age-verification-provider` (histórico),
`selfie-provider` e `selfie-attempts-suspension`; qualquer outro nome agrega em `other`. Todas as combinações
fixas publicam zero quando vazias para não deixar gauges antigos aparentando backlog. Não há rótulos derivados
de IDs, destinatários, payloads ou texto de erro. Ao introduzir consumidor novo, atualizar a lista fixa e os testes.

**Alternativas comparadas.** Excluir todo evento marcado `AwaitingOtherMeasure` ocultaria espera vencida,
um lease expirado ou outra entrega falha/atrasada do mesmo evento. Dividir apenas a idade bruta por consumidor
ainda confundiria espera legítima com atraso. A abordagem escolhida separa elegibilidade de expurgo e de claim,
com inventário por consumidor/estado para preservar visibilidade de falhas e esperas prolongadas. Não altera
regras de suspensão, tentativas, leases, revisão, prazos ou exclusões e é independente do PR #313.

Nenhum alerta novo foi criado. Antes de definir alertas, validar coleta, cardinalidade, cadência e baseline
operacional; avaliar atraso de claim junto de idade/volume e dos sinais de falha existentes. Um consumidor que
reivindica e falha repetidamente pode ter baixo atraso de claim e alto acúmulo de retries. A coleta nova mantém
o gate `Retention:Enabled` e depende do loop de observação existente; se ele estiver desligado/falhar, ausência
de amostras não comprova fila saudável. Falha de observação continua sem impedir a limpeza.

Exclusões efetivadas geram trilha em `ops.audit_events` com quantidade, tipo e job; rollback remove também essa
trilha. Para reverter esta correção, reverter o código de monitoramento, sem migração: a idade bruta anterior
voltará a incluir espera legítima. Ajustes de dashboards devem acompanhar a semântica da versão.

As tabelas originais e os estados não terminais continuam preservados. Jobs atuais só têm Done/Failed:
a limpeza remove Done antigo, nunca Failed nem a identidade recorrente. Estados adicionais do ADR-0021 não
são introduzidos por esta implementação. Retenção de auditoria, e-mail receipts e chat está fora deste expurgo.

Testes de integração usam PostgreSQL descartável: fronteiras, nulos, revisão, lote, recorrência, concorrência,
fencing, rollback e comando administrativo. Não equivalem a ensaio de carga ou validação de produção.
