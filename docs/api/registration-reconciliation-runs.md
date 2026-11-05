# Runs da reconciliação de inscrições legadas (API-ADM26 a API-ADM29)

Fatias "Manifesto" e "Aplicação" do [ADR-0049](../architecture/adr/0049-execucao-da-reconciliacao-de-inscricoes-legadas.md)
([#321](https://github.com/BipoXDXD/duora/issues/321)). Transformam unidades revisadas da
[prévia](registration-reconciliation-preview.md) num manifesto imutável e o aplicam, uma unidade por pedido. Um run não
autoriza uso com dados reais; isso continua exigindo autorização operacional própria, descrita no
[runbook](../operations/registration-reconciliation.md).

- `POST /api/v1/admin/registration-reconciliations/runs` (API-ADM26)
- `GET /api/v1/admin/registration-reconciliations/runs/{runId}` (API-ADM27)
- `POST /api/v1/admin/registration-reconciliations/runs/{runId}/units/{unitId}:apply` (API-ADM28)
- `POST /api/v1/admin/registration-reconciliations/runs/{runId}:close` (API-ADM29)

## Autorização

Todas as rotas usam a sessão normal, **com toque**; o regime sem toque do ADR-0048 continua restrito à prévia.

1. Sessão por cookie (`401 UNAUTHENTICATED`).
2. Sessão administrativa com MFA (`403 FORBIDDEN` para participante; `403 MFA_ENROLLMENT_REQUIRED` sem MFA).
3. Papel `moderator` lido ao vivo (`403 ACCOUNT_MODERATION_FORBIDDEN`), antes de qualquer validação do corpo.
4. Prova de contexto privado em `X-XSRF-TOKEN` e origem (`403 CSRF_VALIDATION_FAILED`).
5. TOTP provado na última hora (`401 REAUTHENTICATION_REQUIRED`).
6. Só na criação: a cota da prévia, 6 pedidos por minuto por conta moderadora, somando prévias e criações, inclusive
   replays e pedidos com chave ou campo inválido (`429 RATE_LIMITED` com `Retry-After`, sem ler o banco). JSON malformado
   e campo desconhecido são recusados antes, no binding, e não gastam cota. Em memória da instância, com a limitação
   registrada no ADR-0048. A chave de configuração continua `RateLimiting:ReconciliationPreviewPerModerator`. A leitura
   de um run não gasta essa cota.
7. O serviço repete 3 e 5 dentro da transação, antes de qualquer replay (`403 ACCOUNT_MODERATION_FORBIDDEN`).

A criação e a aplicação exigem `Idempotency-Key`: de 1 a 64 caracteres `[A-Za-z0-9_-]`, senão
`400 IDEMPOTENCY_KEY_INVALID`. O fechamento exige `If-Match`. A cota do item 6 não vale para aplicar nem para fechar.

## Criar um run

```json
{
  "units": [
    {
      "subjectUserId": "uuid",
      "originalMeasureId": "uuid",
      "unitDigest": "64 caracteres hexadecimais minúsculos"
    }
  ],
  "justification": "até 2000 caracteres",
  "authorizationReference": "até 200 caracteres"
}
```

O corpo é estrito: campo desconhecido ou JSON malformado dá `400 INVALID_REQUEST`. `units` tem de 1 a 10 pares
distintos, o tamanho de uma página da prévia. Os dois textos são obrigatórios, numa linha, sem caracteres de controle.
Campo inválido dá `400 VALIDATION_FAILED`, com `errors` nomeando só o campo. Não trazer dado de participante nos
textos é regra operacional; o código não consegue garanti-la.

Numa transação `REPEATABLE READ`, com `lock_timeout` de 2 s e `statement_timeout` de 10 s, o servidor:

1. revalida a moderação;
2. procura a chave: mesma chave e mesmo pedido devolvem o run existente, em qualquer estado; mesma chave com outro
   pedido dá `409 IDEMPOTENCY_KEY_REUSED`;
3. recusa um segundo run aberto;
4. reclassifica as contas pedidas com o seletor e o corte configurado, e recalcula os digests;
5. com todos iguais, grava o run, as unidades `Pending` e a auditoria num só commit.

| Resposta | Quando | Efeito |
|---|---|---|
| `201` | Run criado, ou replay da mesma chave. `Location` e `ETag` do run | Run, unidades e `ops.registration_reconciliation_run_created` |
| `400` | Chave, corpo ou campo inválido | Nenhum |
| `409 RUN_ALREADY_OPEN` | Já existe um run `Open`. O índice único decide corridas | Nenhum |
| `409 IDEMPOTENCY_KEY_REUSED` | Esta chave deste moderador gravou outro pedido | Nenhum |
| `412 PRECONDITION_FAILED` | Algum par deixou de ser elegível, algum digest divergiu, ou não há corte configurado | Nenhum: refazer a prévia |
| `429 RATE_LIMITED` | Cota compartilhada com a prévia esgotada, com `Retry-After` | Nenhum |
| `503 TEMPORARILY_UNAVAILABLE` | Contenção (`Retry-After: 2`) ou `statement_timeout` (`Retry-After: 30`). Repetir com a mesma chave | Nenhum |

O `412` não diz qual unidade divergiu. Além de mudanças na inscrição ou na medida, entram no digest os tetos, que
dependem da fila de terceiros e do início do evento.

## Ler um run

`GET .../runs/{runId}` devolve `200` com o mesmo corpo da criação, ou `404 NOT_FOUND`. Contenção e timeout dão `503`
com o mesmo `Retry-After` da criação. Qualquer moderador lê qualquer run; esta rota não audita leitura.

## Aplicar uma unidade

`POST .../runs/{runId}/units/{unitId}:apply`, sem corpo. Numa transação READ COMMITTED, com `lock_timeout` de 2 s e
`statement_timeout` de 10 s, o servidor segue o protocolo do ADR-0049:

1. tranca o run `FOR SHARE` e a unidade `FOR UPDATE NOWAIT`;
2. revalida a moderação, antes de qualquer replay ou trabalho;
3. recusa uma chave que já gravou outra unidade; devolve o recibo de uma unidade terminal; recusa unidade `Pending` em run
   fechado;
4. confere se o corte configurado e o seletor do binário são os do run;
5. monta o plano completo (tetos: 50 eventos e 1.000 usuários), tranca guardas e contas e revalida a moderação de novo;
6. tranca a medida e os eventos, relê o plano e, se surgiu recurso novo, recomeça a transação inteira, até três tentativas;
7. reclassifica a unidade sob locks e compara com o manifesto;
8. retira só as inscrições aprovadas, vincula os fatos de promoção e confere os efeitos;
9. grava recibo e auditoria num commit único.

| Resposta | Quando | Efeito |
|---|---|---|
| `200` | Unidade terminal: `Applied`, `SkippedChanged` ou `NeedsReview`, agora ou antes, mesmo com o run fechado e com outra chave | Recibo. Só `Applied` retira e promove |
| `400 IDEMPOTENCY_KEY_INVALID` | Chave ausente ou inválida | Nenhum |
| `403 ACCOUNT_MODERATION_FORBIDDEN` | A autorização caiu em qualquer dos dois pontos | Nenhum |
| `404 NOT_FOUND` | Run inexistente, ou unidade inexistente ou de outro run | Nenhum |
| `409 RUN_CLOSED` | Unidade `Pending` em run fechado | Nenhum |
| `409 CUTOVER_CHANGED` | Corte ausente ou diferente do run, ou run de outro seletor | Nenhum: unidade `Pending` |
| `409 IDEMPOTENCY_KEY_REUSED` | Esta chave deste moderador já gravou outra unidade | Nenhum |
| `401 REAUTHENTICATION_REQUIRED` | TOTP vencido | Nenhum |
| `503 TEMPORARILY_UNAVAILABLE` | Unidade ocupada, contenção, plano instável ou deadlock (`Retry-After: 2`), ou `statement_timeout` (`Retry-After: 30`) | Nenhum |
| `503 TEMPORARILY_UNAVAILABLE` com `Retry-After: 5` | Banco indisponível. Se a conexão caiu no commit, o desfecho é ambíguo: conferir o `state` no `GET` | Desconhecido |
| `500` | Defeito: efeito diferente da liberação do manifesto, acima do teto de promoções ou contagem de vínculos divergente. Sem detalhes, com `traceId` | Rollback integral: unidade `Pending` |

A chave só detecta intento divergente: a idempotência é a do domínio. Uma chave nova numa unidade terminal devolve o recibo.

**Recibo** (allowlist; nada de nome, e-mail, motivo interno nem chave):

| Campo | Significado |
|---|---|
| `runId`, `unitId` | Run e unidade |
| `state` | `Applied`, `SkippedChanged` ou `NeedsReview` |
| `reason` | Motivo fechado, ou `null` em `Applied` |
| `factIds` | Fatos `events.waitlisted_registration_promoted` vinculados; vazio sem promoção |
| `executedByUserId`, `executedAt` | Moderador e instante real, UTC (`Z`) |
| `correlationId` | A mesma correlação da auditoria |

Fora do alcance da unidade: `identity.users`, `safety.moderation_actions`, salas e rodadas, e outras inscrições do sujeito.
O fato v1 e seu payload não mudam; o vínculo é o gate da #320 (ADR-0049, D3).

## Fechar um run

`POST .../runs/{runId}:close`, sem corpo, com `If-Match` do `ETag`. Tranca o run `FOR UPDATE`: espera as unidades em curso, e
as seguintes recebem `409 RUN_CLOSED`.

| Resposta | Quando | Efeito |
|---|---|---|
| `200` | Run fechado, com o corpo de leitura e o `ETag` da versão nova | `Closed` e `ops.registration_reconciliation_run_closed` |
| `403 ACCOUNT_MODERATION_FORBIDDEN` | A autorização caiu | Nenhum |
| `404 NOT_FOUND` | Run inexistente | Nenhum |
| `409 RUN_CLOSED` | `If-Match` na versão atual de um run já fechado | Nenhum |
| `412 PRECONDITION_FAILED` | `If-Match` de outra versão ou de outro run, ou malformado | Nenhum |
| `428 PRECONDITION_REQUIRED` | `If-Match` ausente ou `*` | Nenhum |
| `401 REAUTHENTICATION_REQUIRED` | TOTP vencido | Nenhum |
| `503 TEMPORARILY_UNAVAILABLE` | Unidade em curso além do `lock_timeout` ou deadlock (`Retry-After: 2`), ou `statement_timeout` (`Retry-After: 30`). Repetir | Nenhum |
| `503 TEMPORARILY_UNAVAILABLE` com `Retry-After: 5` | Banco indisponível. Se a conexão caiu no commit, o desfecho é ambíguo: conferir o `GET` | Desconhecido |
| `500` | Falha inesperada. Sem detalhes, com `traceId` | Conferir o `GET` |

## Resposta do run

`Cache-Control: no-store` e `ETag` forte do run. Campos (allowlist; nada de nome, e-mail, motivo interno nem chave de aplicação):

| Campo | Significado |
|---|---|
| `id` | UUIDv7 do run |
| `selectorVersion` | Versão do seletor usada no recálculo |
| `evidenceId`, `cutoverLower`, `cutoverUpper` | Descritor de corte do run, instantes em UTC (`Z`) |
| `createdByUserId` | Moderador que criou |
| `justification`, `authorizationReference` | Como informados |
| `state`, `version` | `Open` ou `Closed`, e a versão do run |
| `createdAt` | Instante da criação, UTC (`Z`) |
| `eTag` | O mesmo valor do header |
| `units[]` | `{id, subjectUserId, originalMeasureId, measureVersion, state, items[], receipt}` |
| `units[].state` | `Pending`, `Applied`, `SkippedChanged` ou `NeedsReview` |
| `units[].items[]` | `{eventId, registrationVersion, state, maxReleasedSeats, maxPromotions}` |
| `units[].receipt` | O mesmo recibo de `:apply`, ou `null` em unidade `Pending`. Mudança aditiva da fatia 3 |

## Persistência

Migração aditiva `20261105120000_RegistrationReconciliationManifest`, no schema `ops`; ver
[model.md](../data/model.md#opsregistration_reconciliation_runs-units-e-unit_facts). O banco garante um só run
aberto, unidades só na transação que criou o run, o manifesto imutável, o recibo imutável quando terminal e o estado
terminal nascido só de `Pending`, mesmo contra um escritor com defeito. Rollback: remover as rotas em release revisada; o `Down` recusa apagar evidência.

Validação: `RegistrationReconciliationRunHttpTests`, `RegistrationReconciliationRunManifestTests`,
`RegistrationReconciliationRunSchemaTests`, `RegistrationReconciliationApplyTests`,
`RegistrationReconciliationApplyConcurrencyTests`, `RegistrationReconciliationApplyRecoveryTests`,
`RegistrationReconciliationApplyHttpTests`, `ReconciliationUnitDigestTests`, `ReconciliationRunOrderTests` e
`ReconciliationUnitRevalidationTests`, com PostgreSQL real e dados sintéticos.
