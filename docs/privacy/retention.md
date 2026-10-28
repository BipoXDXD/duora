# Retenção, exclusão e preservação

> **Decisão vigente — 19/10/2026, issue #93:** verificação etária foi retirada. Participação exige conta Active, e-mail confirmado e selfie de qualidade/presença Passed, além dos demais controles. Referências etárias remanescentes descrevem o sistema anterior ou questões jurídicas históricas; não são requisitos de implementação. Ver [ADR-0029](../architecture/adr/0029-confirmacao-email-presenca-sem-idade.md).


Prazos de guarda, mecanismo de exclusão, preservação e tombstones. Fontes: spec 4.3, 10.7, 12.1, 12.4, 16.2,
18.3, 18.4 e 19.2; [model.md](../data/model.md); [data-map.md](data-map.md); ADR
[0011](../architecture/adr/0011-pgbackrest-wal-externo-restore-obrigatorio.md).

> **Todos os prazos abaixo são propostas operacionais** (spec 12.4), sujeitas a validação de necessidade,
> base legal e obrigações por pessoa qualificada. Este documento não declara conformidade jurídica. A coluna
> "Situação" muda para "aprovado" só com registro da decisão e de quem aprovou.

## Tabela de retenção (spec 12.4)

| Classe | Proposta inicial | Implementação | Tabelas e objetos | Situação |
|---|---|---|---|---|
| Upload abandonado | 24 horas | Job apaga o objeto em quarentena e marca o upload `Deleted` | `media.uploads` (`abandon_after`), prefixo de quarentena | Proposta |
| Original de imagem aprovado | Eliminar após derivação e revisão, salvo finalidade justificada | Job apaga `original_key` depois de gerar variantes e concluir revisão; evidência separada quando legítima | `media.media_assets.original_key` | Proposta |
| Chat temporário de sala | Até 30 dias | Acesso termina ao encerrar o contexto da sala; job apaga mensagens da conversa `Room` 30 dias após o fechamento | `social.conversations` (`kind = 'Room'`), `social.messages` | Proposta |
| Chat persistente | Enquanto conexão e finalidade estiverem ativas; após encerramento,30 dias | Ended/Blocked fecha acesso imediatamente; exclusão após30 dias, salvo preservação específica auditada/com vencimento | `social.conversations` (`kind = 'Persistent'`), `social.messages` | Prazo decidido sob delegação em24/10/2026; módulo/execução ainda pendentes |
| Dados online após pedido de exclusão | Remoção ou anonimização em até 30 dias, salvo exceções documentadas | Desativação e revogação imediatas; etapas em `privacy.deletion_steps`; conclusão exige tombstone externo | Todas as tabelas com `user_id` | Proposta |
| Exportação pronta | 24 horas para obtenção | Reautenticação para baixar (API-D04); job apaga o arquivo e marca `Expired` | `privacy.export_requests`, objeto de exportação | Proposta |
| Idempotência comum | 48 horas | Resultado mínimo; limpeza em lotes; unicidades de negócio continuam | `ops.idempotency_records` | Proposta |
| Telemetria operacional | 14 dias como ponto inicial | Sem conteúdo privado; separada de registros de segurança ou legais | Destino de monitoramento | Proposta |
| Evidência de moderação | 30/90 dias segundo categoria/encerramento/recurso na política de24/10 | `retention_until` obrigatório; preservação específica com vencimento; sem expurgo autorizado por esta decisão | `safety.report_evidence`, `safety.reports` | [Política decidida](moderation.md); implementação/operação pendentes |
| Backups do banco | Janela de recuperação de 14 dias; a cópia expirada continua no bucket até 21 dias de lock + 30 do lifecycle | Expiração consistente de fulls, diferenciais e WAL; compatível com exclusão por tombstones | Repositório pgBackRest no Backblaze B2 (`duora/`) | Proposta |
| Backup das fotos (derivados aprovados) | 14 dias de snapshots; uma foto apagada some da VPS na hora e da cópia externa em até ~65 dias (14 + 21 de lock + 30 do lifecycle) | `restic forget --keep-within 14d --prune` diário; lock e lifecycle do bucket; restore só devolve derivados de assets `Approved` | Repositório restic no Backblaze B2 (`media/`) | Decidido (2026-10-16) |
| Tombstones de exclusão | Pelo menos enquanto houver backup que possa ressuscitar o registro, mais margem aprovada | Registro pseudonimizado (HMAC), protegido e reconciliado no restore | `privacy.deletion_tombstones` e registro externo | Proposta (margem a definir) |
| Registros obrigatórios de acesso | Conforme aplicabilidade e prazo jurídico aprovado | Armazenamento e acesso separados; não aplicar automaticamente a janela de 14 dias | A definir | Pendente jurídico |

## Prazos complementares propostos

Classes que a tabela 12.4 não cobre e que o modelo de dados cria. **Proposta — confirmar.**

| Dado | Proposta | Motivo |
|---|---|---|
| Sessões revogadas ou expiradas (`identity.login_sessions`, `auth_tickets`) | Ticket apagado na revogação ou expiração; linha da sessão mantida 30 dias para a lista de sessões e investigação de abuso | O ticket é segredo e não tem uso depois de inválido |
| Tokens de confirmação e reset | Sem armazenamento (gerados pelo Data Protection); vida de 24 h e 30 min | Nada a apagar |
| Outbox `Delivered`/`Expired` | **Aprovado em 21/10/2026 (#46):** mais de 7 dias desde `completed_at`; evento só após remoção de todas as entregas e mais de 7 dias desde `occurred_at` | Diagnóstico de entrega; o fato de negócio está nas tabelas do módulo |
| Entregas `Failed` | **Aprovado em 21/10/2026 (#46):** preservar até revisão explícita; depois excluir apenas com `completed_at` há mais de 30 dias. Alertar aos 30 dias sem revisão, sem apagar | Fila de revisão com causa sanitizada |
| Jobs concluídos (`ops.jobs`) | 7 dias; as linhas fixas dos tipos recorrentes (uma por tipo) não são apagadas | Idem |
| Jobs `Failed` (`ops.jobs`) | Até resolução (reprocessar ou descartar como `Cancelled`, com auditoria); não são apagados antes disso | Um job de correção de estado falho pode deixar sala ou rodada parada; apagá-lo esconderia o defeito. O payload só tem IDs ([ADR-0021](../architecture/adr/0021-jobs-duraveis-e-leases-no-postgresql.md)) |
| Recibos de webhook (`ops.webhook_receipts`) | **Aprovado em 21/10/2026 (#46):** mais de 30 dias desde `processed_at`, somente `Processed`/`Rejected`; data ausente ou `Received` permanece | Janela de replay do fornecedor mais margem |
| Recibos de e-mail (`ops.email_receipts`) | 30 dias | Diagnóstico de entrega e supressão |
| Auditoria (`ops.audit_events`) | 1 ano para ações privilegiadas e de segurança **(hipótese)**; `actor_id` pseudonimizado na exclusão da conta | Revisão de moderação, recurso e incidentes (INV-10) |
| Selfie de presença (`identity.selfie_verifications`) | **Aprovado em 28/10/2026 (#93):** nenhuma imagem, vídeo, score ou template guardado. O resultado `Passed` (estado, política e datas) fica enquanto a conta existir ("a selfie fica guardada mesmo após 30 dias e a pessoa não perde participação"); tentativas não aprovadas saem 30 dias após a conclusão, ou após a expiração se nunca concluíram; referência da sessão AWS e feedback saem 24 h após a expiração; a exclusão física da conta apaga tudo (FK em cascata) | Prova de presença sem recoleta periódica; limpeza no worker (`SelfieVerificationDispatcher`), testada em `SelfieRetentionTests` |
| Verificações etárias substituídas ou expiradas | Manter só a mais recente decisiva por conta; anteriores apagadas após 90 dias **(hipótese)** | Rastreabilidade do resultado sem acumular histórico |
| Decisões privadas (`social.decisions`) | Apagar 30 dias após o fim da janela quando não houve conexão | A decisão negativa não tem uso depois da janela e é dado íntimo |
| Pares e reservas (`events.pairings`) | Enquanto o evento puder ser revisado; proposta de 90 dias após o evento | Não repetir par no evento e investigar incidentes |
| Denúncias encerradas sem medida | Prazo por categoria definido na política de moderação | Ver "Evidência de moderação" |

## Exclusão de conta

Sequência de API-D03, registrada em `privacy.deletion_steps` (spec 12.4, 18.4):

1. **Imediato, na mesma transação do pedido:** conta `DeletionPending`, todas as sessões revogadas, hub
   fechado, entregas pendentes canceladas, perfil e fotos fora de qualquer projeção, inscrições futuras
   retiradas, conexões encerradas.
2. **Etapas do job, cada uma idempotente e com checkpoint:** perfil e preferências; fotos e variantes no
   storage (inclusive versões antigas do objeto); mensagens de conversas da pessoa; decisões e estado de jogo;
   exportações; verificação etária; notificações; avaliação de `safety` contra `retention_holds`; pedido de
   exclusão aos fornecedores que guardam dados.
3. **Tombstone externo** confirmado (HMAC do identificador) antes de considerar a exclusão concluída;
   enquanto falhar, o pedido fica pendente e alerta (spec 18.4).
4. **Conta:** linha de `identity.users` anonimizada e, por fim, apagada; `ops.audit_events.actor_id`
   pseudonimizado.

Como as referências a `identity.users` vindas de outros módulos são `ON DELETE RESTRICT`
([model.md](../data/model.md#decisões-de-modelagem-propostas), D6), a conta só some depois que cada módulo
cumpriu a sua etapa; uma etapa esquecida falha a exclusão em vez de deixar dado órfão.

"Remoção ou anonimização" exige técnica verificada: tirar o nome sem eliminar a relacionabilidade não torna o
dado anônimo. Preferir exclusão quando a finalidade terminou (spec 12.4).

### Dados compartilhados com outra pessoa

| Dado | Proposta — confirmar |
|---|---|
| Mensagens enviadas pela pessoa excluída em conversa com outra | Apagadas; a outra pessoa vê a conversa encerrada sem o histórico do autor excluído |
| Mensagens recebidas pela pessoa excluída | Seguem a retenção da conversa encerrada para a outra pessoa |
| Conexão e decisões | Apagadas; a outra pessoa vê a conexão como encerrada |
| Denúncias feitas pela pessoa | `reporter_id` anulado depois da avaliação de preservação; o caso e a evidência seguem a política de moderação |
| Denúncias contra a pessoa e medidas aplicadas | Avaliadas contra `retention_holds` (recurso pendente, preservação aprovada); sem preservação, seguem o prazo da categoria |

## Preservação

`privacy.retention_holds` registra preservação obrigatória ou aprovada (spec 12.4):

- sempre com escopo (conta, denúncia ou evidência), motivo, referência da decisão, quem aprovou e
  **vencimento**; `CHECK` impede preservação sem vencimento;
- suspende a exclusão só do recorte preservado, nunca da conta inteira;
- vencida ou liberada, o recorte volta à regra normal na próxima execução do job;
- criar ou liberar preservação é ação privilegiada auditada.

## Backups e tombstones

Não é possível apagar instantaneamente cada backup imutável (spec 12.4). A estratégia é impedir a reexposição:

1. O tombstone é gravado no banco e num registro externo protegido, idempotente, antes de concluir a exclusão.
2. Todo restore, antes de reabrir, aplica os tombstones posteriores ao ponto restaurado e revoga todas as
   sessões; falha fechado se o registro externo estiver indisponível (spec 18.4; T-11).
3. Backups expiram pela janela de 14 dias; o tombstone vive pelo menos até o último backup que contém o
   registro expirar, mais a margem aprovada. No bucket B2, a cópia expirada ainda fica até 21 dias (lock
   `compliance`) mais 30 (lifecycle): a margem do tombstone precisa cobrir esses ~65 dias.
4. Fotos restauradas passam por um staging e só voltam as de assets `Approved` no banco já restaurado
   ([backup-restore.md](../operations/backup-restore.md#restore-das-fotos)).
5. Ensaios de restore usam ambiente restrito e apagam a cópia e seus segredos ao final (spec 18.5).

## Execução dos jobs de retenção

- Lotes pequenos com limite de tempo e checkpoint; nunca uma transação gigante (spec 10.7).
- Fora do horário de eventos quando possível (spec 15.1).
- Métricas por classe: itens elegíveis, apagados, falhos e idade do item mais antigo elegível.
- Exclusão no storage é repetível: objeto já ausente conta como concluído (spec 16.2).
- Parar e escalar em preservação aprovada, divergência de tombstone ou falha de fornecedor (spec 14.4).

## Lacunas

1. **Chat persistente:** prazo de30 dias decidido sob delegação em24/10/2026; falta implementar e comprovar o módulo/expurgo, sem afirmar tabela existente na main atual.
2. **Evidência de moderação:** [política de24/10](moderation.md) resolve a decisão de prazos; falta a implementação/execução controlada e sua revisão, sem remover preservação válida.
3. **Registros de acesso:** aplicabilidade e prazo jurídicos ([data-map.md](data-map.md#lacunas), item 4).
4. **Margem dos tombstones** e custódia da chave de HMAC (sem a chave, o tombstone não reconhece o registro;
   com ela vazada, o tombstone é reversível por força bruta sobre e-mails).
5. **Logs do fornecedor de e-mail e do fornecedor etário:** retenção depende do contrato escolhido.

## Execução operacional aprovada (#46)

As aprovações de 21/10/2026 acima são específicas; não ratificam os demais prazos propostos nesta página.
O worker executa lotes de até 1.000 por tabela, mantendo a identidade recorrente e o cutoff do backlog.
Falhas da retenção têm até oito tentativas e exigem retomada auditada após esgotar; nenhuma falha não
revisada é descartada por idade. Revisão tem ator/data/motivo fechado e permanece na auditoria após expurgo.
Veja [o procedimento operacional](../operations/retention.md), incluindo ativação e rollback.


## Revisão humana de fotos — contrato M0, implementação pendente

Os prazos aceitos do ADR-0035 estão detalhados no [contrato de revisão](../api/media-review.md#relógio-recurso-retenção-e-perda-de-bytes),
incluindo intervalo UTC, recurso versus limpeza e teto de30dias sem aprovação automática. O registro mínimo
de decisão/auditoria é de1ano, sem imagem/texto sensível; candidato privado fica fora do backup de mídia,
mas metadados seguem no backup do banco. Preservação excepcional continua exigindo escopo e vencimento
explícitos; recurso não cria hold ilimitado. Jobs, tombstones e reconciliação editorial ainda não estão
implementados. Esta referência não muda retenção executável nem autoriza expurgo, coleta ou ativação.
