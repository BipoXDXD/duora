# ADR-0042: Suspensão de conta por violação dos Termos, com sessão restrita negada por padrão

- **Status:** Proposed (2026-10-30), aguardando a revisão humana exigida para bloqueio e autorização (AGENTS.md).
- **Origem:** issue #299; decisão do dono de 29/10/2026 (#296): o app bloqueia o uso de quem viola os Termos. Spec 4.2
  (estado `Suspended`), 8.2 (sessão restrita), 8.5 (MFA, permissão específica e auditoria), 12.3 e INV-06/INV-10.
- **Revisar quando:** a issue #300 fechar as regras da reprovação na selfie; entrar o recurso de moderação (API-S06,
  API-ADM15); houver um segundo moderador; ou existir denúncia (`safety.reports`) e versão de política
  (`safety.policy_versions`) persistidas.

## Contexto

Havia só o modelo de domínio (`AccountStatus.Suspended`, `ModerationAction`). Nada gravava a suspensão, o login e a
sessão ignoravam o status, e cada rota decidia sozinha o que uma conta fora do fluxo podia fazer (perfil, fotos e
bloqueio aceitavam `Suspended`). Faltavam quatro decisões:

1. **Onde mora a medida.** O modelo proposto em [model.md](../../data/model.md#safetymoderation_actions) liga a medida a
   denúncia, versão de política e aviso, tabelas que ainda não existem.
2. **Como a sessão obedece.** A spec quer que a conta suspensa entre, mas só alcance a própria conta, a segurança e
   o recurso.
3. **Quem pode suspender.** O dono é o único moderador (ADR-0035, 28/10/2026).
4. **Como a issue #300 reaproveita o mecanismo.**

Alternativas consideradas:

- **Revogar as sessões ao suspender (security stamp).** Efeito imediato, mas tira da pessoa o acesso à própria conta
  e à segurança, que a spec 8.2 manda manter, e a obriga a entrar de novo só para ver o motivo.
- **Checar o status em cada handler ou policy.** Repete a regra em dezenas de rotas, e rota nova nasceria aberta.
- **Allowlist central no pipeline, lida do banco a cada requisição (escolhida).** Uma regra, negar por padrão, com
  um teste que enumera as rotas.
- **Medida só no Safety, sem mudar `identity.users.status`.** Evita tocar o Identity, mas a sessão teria de consultar
  o Safety a cada requisição e o estado da conta (spec 4.2) mentiria.
- **Tabela de estado separada (`active_suspensions`) com linha por conta.** Uma tabela a mais para a mesma garantia
  que o índice único parcial já dá.

## Decisão

**Persistência.** `safety.moderation_actions` guarda a medida: conta, `measure` (`Suspension` por enquanto),
`reason_code` (`TermsViolation` | `SelfieVerificationFailed`), motivo interno (1 a 2000 caracteres, só auditoria),
autor (`Human` com id ou `Automated` sem id), instante, chave e hash do comando, `version` e as colunas `lifted_*` da
revogação. Uma suspensão ativa por conta é o índice único parcial
`ux_moderation_actions_active_suspension (subject_user_id) WHERE measure = 'Suspension' AND lifted_at IS NULL`; o
INSERT usa `ON CONFLICT DO NOTHING` sem alvo, então duas decisões simultâneas criam uma só medida sem
check-then-insert. A decisão gravada é imutável (trigger `safety.keep_moderation_decision`); a revogação preenche
`lifted_*` uma única vez e sobe `version` de 1 para 2. O app não apaga linhas (`REVOKE DELETE, TRUNCATE`). FKs para
`identity.users` com `RESTRICT` e índice próprio. CHECKs: autor coerente, `TermsViolation` só com autor humano,
autor diferente da conta, revogação completa ou ausente.

O subconjunto do modelo proposto fica assim de propósito: `report_id`, `policy_version_id` e o aviso entram por
migração aditiva quando existirem denúncias e versões de política. Não há `effective_until`: a spec 4.2 proíbe a
volta automática a `Active` por decurso de prazo.

**Estado da conta.** Na mesma transação, o Identity (dono de `identity.users`) move a conta para `Suspended` e guarda
o estado anterior em `status_before_suspension` (`PendingEligibility`, `Active` ou `Restricted`). A revogação não copia
esse estado às cegas: ela o recalcula pela regra do domínio `ParticipationEligibility.AfterPresenceCheck`. Uma conta
em `PendingEligibility` com e-mail confirmado e selfie aprovada durante a suspensão volta como `Active`, porque o worker
só promove quem está em `PendingEligibility` e não a promoveu enquanto ela estava suspensa. O CHECK
`ck_users_status_before_suspension` só admite a coluna preenchida com `status = 'Suspended'`. Por isso, sair de
`Suspended` por qualquer caminho exige limpar a coluna na mesma instrução. A revogação já faz isso; o futuro fluxo de
exclusão de conta (API-D03) precisa fazer o mesmo, ou a transição falha no CHECK. A revogação só restaura conta que
ainda está em `Suspended` com estado anterior registrado; nenhum outro caminho de saída existe hoje. Só participantes são suspensos: conta
inexistente ou privilegiada dá o mesmo 404; `PendingEmail` e `DeletionPending` dão 409.

**Sessão.** `LoginSessions.TouchAsync`, que já roda a cada requisição, lê `status = 'Suspended'` junto com papéis e
stamp. O `SuspendedAccountGate` roda entre autenticação e autorização e responde 403 `ACCOUNT_SUSPENDED` a toda rota
com sessão que não tenha a marca `AllowForSuspendedAccount()`. Rota pública segue igual. A sessão não é revogada: a
suspensão vale na requisição seguinte de toda sessão aberta, e a revogação devolve o acesso do mesmo jeito.
Convites e salas ativas da pessoa são abortados, e as reservas liberadas, como no bloqueio (spec 7.3).

Allowlist: `GET /me` (com `suspension: { reasonCode, suspendedAt }`), `GET /me/sessions`,
`DELETE /me/sessions/{id}`, as quatro rotas de `/me/mfa`, `POST /auth/reauthenticate`, `POST /me/password-changes`
e, por privacidade, `GET /me/photos`, `GET /me/photos/{id}/status` e `DELETE /me/photos/{id}` (a pessoa acha e
exclui as próprias fotos; não envia novas). O recurso (API-S06) entra quando existir.

**Rotas administrativas.** No grupo `/admin` (sessão administrativa com MFA): `GET /admin/accounts/{userId}/suspension`,
`POST /admin/accounts/{userId}/suspensions` (`Idempotency-Key`; motivo `TermsViolation` e texto obrigatórios) e
`POST /admin/accounts/{userId}/suspensions/{id}/lift` (`If-Match` com o ETag da medida; motivo obrigatório). Os comandos
exigem TOTP recente (ADR-0041). Um filtro no grupo confere o papel antes de qualquer validação de corpo ou de
`Idempotency-Key`, e o serviço o confere de novo antes de criar ou travar a guarda do alvo e depois das guardas. A permissão específica é o papel `moderator`, conferido ao vivo na transação, junto com
conta ativa com MFA e sessão administrativa válida; `product_admin` e `operator` recebem 403. Auditoria em
`ops.audit_events` (`safety.account_suspended` v1 e `safety.account_suspension_lifted` v2, alvo `moderation_action`).

**API interna.** `AccountSuspensions.SuspendAutomaticallyAsync(SuspensionOrder.Automated(...),
ModerationCommandKey.ForSystem(origem, idDaOrigem), ct)`. Ela abre a própria transação e é idempotente pela chave.
Se a conta já está suspensa por outra medida, devolve `AlreadySuspended`. É a porta da issue #300. A medida guarda a origem
(`source_code`, `source_id`; por exemplo, `selfie-verification` e o id da verificação) e a auditoria registra
`safety.account_suspension_source_recorded` com a origem como alvo, que é a "evidência utilizada" da spec 12.3. Nada
de dado biométrico entra: só o código da origem e o id. A automação só
suspende por `SelfieVerificationFailed`: violação dos Termos segue exigindo pessoa (spec 12.3, TM-34). Essa é a
exceção a `SafetyRule.MeasureRequiresHumanAuthor`, condicionada às decisões da #300.

**Adendo (2026-10-30, issue #300).** A exceção fica registrada no
[ADR-0043](0043-limite-de-reprovacoes-da-selfie.md), com a decisão do dono de 29/10/2026: a décima reprovação da selfie
suspende a conta automaticamente, pela porta interna e fora da transação do resultado, com a chave
`system:selfie-verification:{verificationId}`. Revogar essa suspensão apaga as reprovações da selfie na mesma
transação (operação do Identity), e a conta volta ao primeiro ciclo.

## Consequências

- Uma rota nova nasce negada para a conta suspensa. Abri-la exige a marca e muda a allowlist de
  `SuspendedAccountRoutesTests`, que aparece na revisão.
- Comportamento que muda: a conta suspensa não lê nem edita o perfil e não bloqueia ninguém. Antes, as rotas aceitavam
  `Suspended`, e `BlockEligibility` ainda aceita no domínio, mas a borda nega antes.
- Duas fontes descrevem a suspensão (a linha ativa e `users.status`). Só este serviço grava as duas, na mesma
  transação. Não há FK entre elas; uma conta posta em `Suspended` por SQL manual não tem estado anterior, e a
  revogação não a reativa.
- A FK `RESTRICT` de `subject_user_id` (e das colunas de autor) impede apagar fisicamente uma conta com medida de
  moderação. O expurgo da spec 12.4 terá de decidir, com a política de retenção, se anonimiza ou retém a medida
  (INV-10) antes de apagar a conta.
- A suspensão não libera inscrições `Confirmed` ou `CheckedIn` em `events.registrations`: a vaga continua ocupada e a
  lista de espera não é promovida. A issue #305 trata isso.
- Sem recurso no app: a tela diz que o pedido de revisão ainda não está disponível. Com um moderador só, a
  independência do recurso (spec 12.3) precisa de decisão própria.

### Decisões pendentes do dono

1. **Excluir foto durante a suspensão.** Hoje a conta suspensa lista, consulta e exclui as próprias fotos (privacidade,
   LGPD). Em contrapartida, isso pode apagar algo que serviria de evidência. A exclusão de foto já preserva a evidência
   separadamente quando aplicável (API-M03), mas a decisão é do dono.
2. **Bloquear pessoas durante a suspensão (API-S01).** Hoje a conta suspensa não bloqueia. `BlockEligibility` ainda
   aceita `Suspended`, e a arquitetura de informação põe a conta suspensa na área de Segurança. Manter ou liberar o
   bloqueio é decisão do dono.

### STRIDE

| Ameaça | Mitigação | Teste |
|---|---|---|
| S: sessão sequestrada de moderador suspende alguém | MFA, sessão administrativa, TOTP recente (1 h) | `WithoutARecentTotpTheModeratorMustReauthenticateAndNothingIsWritten` |
| T: reescrever a decisão ou apagar a medida | Trigger de imutabilidade e `REVOKE DELETE` | `TheRecordedDecisionCannotBeRewrittenNorDeletedByTheApp` |
| R: moderador nega ter suspendido | Auditoria na transação, com autor e versão | `SuspendingAndLiftingAreAuditedWithTheModeratorAndTheMeasure` |
| R: suspensão automática sem a evidência utilizada | Origem na medida e na auditoria | `TheSystemSuspendsThroughTheInternalApiIdempotentlyAndAudited` |
| T: chave de idempotência de um moderador reaproveitada por outro | Escopo da chave por moderador | `TheSameKeyFromTwoModeratorsCreatesIndependentCommands` |
| I: suspenso vê motivo interno ou o moderador | `/me` só com código e instante | `TheSuspendedPersonReadsOnlyTheMinimalStateOfTheOwnAccount` |
| I: enumerar contas privilegiadas pela rota | 404 igual para inexistente e privilegiada | `AnUnknownOrPrivilegedTargetIsTheSameNotFoundWithoutItsId` |
| D: duas suspensões simultâneas | Índice único parcial | `TwoSimultaneousSuspensionsOfTheSameAccountCreateOneMeasure`, `TheDatabaseRefusesASecondActiveSuspensionEvenWithoutTheService` |
| E: sessão suspensa usa rotas de participação | Gate negar por padrão | `EveryRouteWithSessionOutsideTheAllowlistIsForbiddenToTheSuspendedAccount`, `AnOpenSessionLosesParticipationOnTheVeryNextRequestAfterTheSuspension` |
| E: papel administrativo genérico modera | Papel `moderator` conferido ao vivo, antes de validar o pedido | `AProductAdministratorOrOperatorWithMfaCannotModerateAccounts`, `AProductAdministratorIsForbiddenBeforeTheBodyOrKeyIsValidated` |
| D: revogação deixa a conta presa em `PendingEligibility` | Estado recalculado pela regra de elegibilidade | `LiftingPromotesAPendingAccountWhosePresenceWasApprovedDuringTheSuspension` |

## Compliance

- Unidade: `AccountSuspensionTests` (Core), com pedido, autor, motivo, tetos e chaves.
- Integração: `Safety.AccountSuspensionTests`, `SuspendedAccountRoutesTests` e `AccountSuspensionRoomTests`, além
  das matrizes que já existiam (`AppRolePrivilegesTests`, `CsrfEveryRouteTests`, `EndpointAuthorizationTests`,
  `AdminRouteTests`).
- E2E: `account-suspension.spec.ts`, com axe em PT-BR e EN-US.
