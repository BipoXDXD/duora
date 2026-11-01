# Recuperação administrativa de suspensão por selfie (API-ADM24)

Implementa a ferramenta que faltava no procedimento do PR #315, conforme a #319 e o
[ADR-0043](../architecture/adr/0043-limite-de-reprovacoes-da-selfie.md#recuperação-manual-de-entrega-morta-306).
Destina-se ao moderador que decide recuperar o efeito de uma entrega morta de `selfie-attempts-suspension`.
Não há UI nem CLI novos; o cliente administrativo usa a autenticação HTTP existente.

`POST /api/v1/admin/accounts/{userId}/selfie-suspension-recoveries/{deliveryId}`

Exige cookie de sessão administrativa real, MFA, papel `moderator`, TOTP recente (janela do ADR-0041), prova
do contexto privado/CSRF em `X-XSRF-TOKEN` e origem permitida. `product_admin`, `operator` e participantes não
recebem essa permissão. A sessão é revalidada na transação antes e depois das guardas de usuários; IDs enviados
pelo cliente nunca constituem uma sessão. O motivo é fixado pelo servidor como `SelfieVerificationFailed`.

Headers: `Idempotency-Key` estável, em `[A-Za-z0-9_-]{1,64}`; demais headers de segurança da sessão existente.
Corpo JSON: `{ "internalReason": "referência privada e justificativa do incidente" }`, de 1 a 2.000 caracteres,
com as mesmas regras de texto da suspensão. Não incluir dados de participantes em exemplos, issues ou logs.

A transação existente de suspensão trava as guardas e a conta, valida a entrega `Failed`, concluída e não
revisada, o consumidor, o tipo/versão/recurso do evento e o vínculo `resource_id → selfie_failures → userId`.
Sob a mesma trava da conta, confirma `Exhausted` pelo ledger atual; aprovação, revogação da selfie e exclusão
são serializadas com essa decisão. A entrega fica travada até o commit para impedir corrida com revisão ou
retenção. Não se usa o payload, nem se exige a tentativa original, que pode ter sido expurgada após 30 dias.

| Resultado | Ação do moderador |
|---|---|
| `201` + `AccountSuspensionResponse`, `Location` e `ETag` | Conferir motivo, autoria humana (`decidedAutomatically: false`), estado atual e auditoria. Um replay devolve a mesma medida e pode conter `liftedAt`; não significa nova suspensão ativa. |
| `409 SELFIE_RECOVERY_UNAVAILABLE` | Entrega ausente/incompatível/revisada, ou vínculo com o ledger ausente. Interromper e investigar; não inferir identidade nem trocar a chave para forçar. |
| `409 SELFIE_RECOVERY_NOT_REQUIRED` | Vínculo válido, mas as tentativas atuais não estão esgotadas. Nenhuma suspensão aplicada; confirmar e documentar o desfecho privado. |
| `409 ACCOUNT_ALREADY_SUSPENDED` | Reler `GET /admin/accounts/{userId}/suspension`. Se por selfie, conferir a medida existente. Se por outro motivo, manter pendente até sua revogação; não substituir essa medida. |
| `409 ACCOUNT_NOT_SUSPENDABLE` / `404 NOT_FOUND` | Investigar estado incompatível, inexistência ou conta privilegiada. Nenhuma medida aplicada. |
| `409 IDEMPOTENCY_KEY_REUSED` | A chave pertence a pedido diferente (inclui conta, entrega e motivo interno). Investigar o pedido original. |
| `503 TEMPORARILY_UNAVAILABLE`, com `Retry-After` | Contenção: rollback integral; repetir de forma controlada o mesmo pedido com a mesma chave. |
| `400 IDEMPOTENCY_KEY_INVALID` / `400 VALIDATION_FAILED` | Corrigir o formato antes da intervenção, sem alterar uma intenção já confirmada. JSON/campos extras também são recusados. |
| `401` / `403` | Aplicam-se os códigos existentes de sessão, MFA, step-up, papel e CSRF. Reautenticar pelo fluxo normal; nunca fabricar identidade administrativa. |

**Resultado incerto:** perda da conexão ou erro sem resposta de sucesso não comprova rollback. O PostgreSQL
pode ter confirmado a medida antes da perda da confirmação. Manter o incidente pendente, conservar o mesmo
moderador com sessão autorizada (reautenticando se necessário), chave, conta, entrega e motivo interno, e
repetir o pedido de forma controlada. Conferir a medida retornada, a leitura administrativa e a auditoria
antes de concluir a revisão da entrega; não trocar a chave nem aplicar outra medida para compensar uma
resposta perdida.

A medida humana passa pela mesma operação que retira inscrições com histórico, libera reservas, aborta salas,
atualiza a versão de segurança e audita. `safety.account_suspended` aponta para a medida;
`safety.account_suspension_source_recorded` aponta para a entrega (`outbox_delivery`), com ator `Moderator`,
na mesma transação. Replays não repetem efeitos nem auditoria. A chave e a intenção são persistidas como hashes;
a referência privada ao incidente fica no motivo interno e no registro privado, nunca na resposta ao participante.

A entrega permanece `Failed`, com tentativas e payload inalterados. Após confirmar a medida ou o fim da
necessidade, o operador autorizado registra a revisão pelo
[runbook de retenção](../operations/retention.md#revisar-falha-de-entrega). O endpoint não executa esse comando
automaticamente. `accepted-loss` exige aceitação humana explícita. Conservar as referências privadas antes da
revisão: a retenção pode expurgar a entrega antiga na próxima execução.

Não reenfileirar o evento nem alterar o ledger, o status da entrega ou as tabelas de moderação por SQL manual.
Não mascarar a recuperação como `TermsViolation`; a rota comum de suspensão permanece restrita a esse motivo.
Após revogação humana, repetir a chave só lê a medida revogada; uma chave nova não recupera o vínculo apagado.
Tentativas, suspensão automática e seus limites não mudam.

Validação: `SelfieSuspensionRecoveryTests` usa dados sintéticos e PostgreSQL real para permissões, payload limpo,
idempotência, auditoria, revogação, revisão, contenção e barreiras de concorrência. O teste
`ALostCommitConnectionCanBeRetriedWithTheSameKeyWithoutDuplicatingTheHumanMeasure` corta a conexão da operação
de aplicação antes do COMMIT e depois do COMMIT sem entregar sua confirmação; a repetição por HTTP observa
o resultado persistido, com uma única medida humana e auditoria, sem revisar ou reenfileirar a entrega.
Não valida incidentes reais.
O merge exige revisão humana de bloqueio/autorização. Rollback: reverter a rota e sua implementação em release
revisada; não apagar medidas/auditorias nem resetar entregas. Na ausência da ferramenta, escalar e manter pendente.
