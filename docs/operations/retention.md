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

O meter `Duora.Retention` publica contagem de linhas e idade mais antiga das quatro tabelas, exclusões por
tabela, duração do lote, falhas de entrega sem revisão acima de 30 dias e jobs de retenção falhos. O worker
consulta os totais uma vez por minuto e emite aviso agregado quando há revisão pendente. Conecte esse meter
ao coletor usado no ambiente; os instrumentos não comprovam que um backend externo de métricas esteja configurado.
Os rótulos são somente nomes fixos de tabela, nunca IDs, destinatários ou conteúdo. Exclusões efetivadas geram
trilha em `ops.audit_events` com quantidade, tipo e job; rollback remove também essa trilha.

As tabelas originais e os estados não terminais continuam preservados. Jobs atuais só têm Done/Failed:
a limpeza remove Done antigo, nunca Failed nem a identidade recorrente. Estados adicionais do ADR-0021 não
são introduzidos por esta implementação. Retenção de auditoria, e-mail receipts e chat está fora deste expurgo.

Testes de integração usam PostgreSQL descartável: fronteiras, nulos, revisão, lote, recorrência, concorrência,
fencing, rollback e comando administrativo. Não equivalem a ensaio de carga ou validação de produção.
