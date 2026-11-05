# Runs da reconciliação de inscrições legadas (API-ADM26 e API-ADM27)

Fatia "Manifesto" do [ADR-0049](../architecture/adr/0049-execucao-da-reconciliacao-de-inscricoes-legadas.md)
([#321](https://github.com/BipoXDXD/duora/issues/321)). Transforma unidades revisadas da
[prévia](registration-reconciliation-preview.md) num manifesto imutável. Não aplica nada: `:apply`, `:close`, runbook
e tela ficam para a etapa seguinte. Um run não autoriza uso com dados reais; isso continua exigindo autorização
operacional própria.

- `POST /api/v1/admin/registration-reconciliations/runs` (API-ADM26)
- `GET /api/v1/admin/registration-reconciliations/runs/{runId}` (API-ADM27)

## Autorização

As duas rotas usam a sessão normal, **com toque**; o regime sem toque do ADR-0048 continua restrito à prévia.

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

Só a criação exige `Idempotency-Key`: de 1 a 64 caracteres `[A-Za-z0-9_-]`, senão `400 IDEMPOTENCY_KEY_INVALID`.

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

## Resposta

`Cache-Control: no-store` e `ETag` forte do run. Campos (allowlist; nada de nome, e-mail, motivo interno, recibo):

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
| `units[]` | `{id, subjectUserId, originalMeasureId, measureVersion, state, items[]}` |
| `units[].state` | `Pending`, `Applied`, `SkippedChanged` ou `NeedsReview` |
| `units[].items[]` | `{eventId, registrationVersion, state, maxReleasedSeats, maxPromotions}` |

## Persistência

Migração aditiva `20261105120000_RegistrationReconciliationManifest`, no schema `ops`; ver
[model.md](../data/model.md#opsregistration_reconciliation_runs-units-e-unit_facts). O banco garante um só run
aberto, unidades só na transação que criou o run, o manifesto imutável, o recibo imutável quando terminal e o estado
terminal nascido só de `Pending`, mesmo contra um escritor com defeito. Rollback: remover as rotas em release revisada; o `Down` recusa apagar evidência.

Validação: `RegistrationReconciliationRunHttpTests`, `RegistrationReconciliationRunManifestTests`,
`RegistrationReconciliationRunSchemaTests`, `ReconciliationUnitDigestTests` e `ReconciliationRunOrderTests`, com
PostgreSQL real e dados sintéticos.
