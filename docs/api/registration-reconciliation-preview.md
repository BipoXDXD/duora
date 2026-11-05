# Prévia da reconciliação de inscrições legadas (API-ADM25)

Fatia 2 da [#321](https://github.com/BipoXDXD/duora/issues/321): expõe o leitor da fatia 1 numa rota
administrativa de leitura, sem nenhuma mutação lógica no banco. Não cria run, não aplica nada e não autoriza uso com
dados reais; isso continua exigindo autorização operacional própria
([ADR-0044](../architecture/adr/0044-inscricoes-de-contas-suspensas.md) e seção 14 da proposta da #321).

`GET /api/v1/admin/registration-reconciliations/preview?cursorSubject={uuid}&cursorMeasure={uuid}`

## Autorização

Na ordem em que é verificada; de 1 a 6, antes de ler o cursor:

1. Sessão por cookie (`401 UNAUTHENTICATED` sem ela). A sessão é **lida sem toque**
   ([ADR-0048](../architecture/adr/0048-leitura-de-sessao-sem-toque-na-previa-de-reconciliacao.md)): a prévia não
   registra uso, não desliza a inatividade, não renova cookie e não revoga.
2. Sessão administrativa com MFA (`403 FORBIDDEN` para participante; `403 MFA_ENROLLMENT_REQUIRED` sem MFA).
3. Papel `moderator` lido ao vivo (`403 ACCOUNT_MODERATION_FORBIDDEN`; `operator` e `product_admin` sozinhos não
   bastam). Banco indisponível nessa leitura: `503 TEMPORARILY_UNAVAILABLE` com `Retry-After`.
4. Prova de contexto privado em `X-XSRF-TOKEN` (`403 CSRF_VALIDATION_FAILED`), como toda leitura privada.
5. TOTP provado na última hora, intervalo fechado do ADR-0041; prova futura não vale
   (`401 REAUTHENTICATION_REQUIRED`). O step-up é a rota existente de reautenticação, que escreve e é separada da
   prévia.
6. Limite de 6 prévias por minuto por conta moderadora (inclusive pedidos com cursor inválido):
   `429 RATE_LIMITED` com `Retry-After`, sem ler o banco. Em memória da instância; com mais de uma réplica, cada
   uma contaria à parte (limitação registrada no ADR-0048).
7. O leitor repete a verificação 3 e 5 dentro do snapshot `REPEATABLE READ READ ONLY` antes de ler inscrições;
   se ela falhar ali, `403 ACCOUNT_MODERATION_FORBIDDEN`.

A checagem do papel na policy (passo 3) é um SELECT em autocommit, fora do snapshot; a garantia de não escrever vem
da revalidação no snapshot `READ ONLY` e é conferida pelo fingerprint dos testes.

A identidade vem só da sessão; o cliente não informa conta nem sessão.

## Parâmetros

`cursorSubject` e `cursorMeasure` vêm juntos ou não vêm: são o `next` da página anterior. Cada um é UUID no
formato canônico com hífens, diferente de zero. Um só, vazio, outro formato, repetido ou com caracteres de
controle: `400 VALIDATION_FAILED` com `errors.cursor = ["INVALID_FORMAT"]`, sem ecoar o valor.

O cliente não envia timestamp nem evidência. O corte vem da configuração do servidor (seção abaixo).

## Timeout

Cada consulta do snapshot tem `statement_timeout` de 10 s. Estourado, `503 TEMPORARILY_UNAVAILABLE` com
`Retry-After: 30`, corpo só com campos de Problem Details e nenhuma escrita. Repetir depois da espera; não
insistir em sequência (o limite por minuto continua contando).

## Resposta `200`

`Cache-Control: no-store`. Campos (allowlist; nada de nome, e-mail, motivo interno, selfie ou perfil):

| Campo | Significado |
|---|---|
| `asOf` | Instante UTC (`Z`) do snapshot |
| `selectorVersion` | Versão do seletor (`LegacyRegistrationSelector.Version`) |
| `evidenceId` | Descritor de corte configurado, ou `null` sem corte |
| `registrationsByPrimaryIssue[]` | `{issue, registrations}` por motivo principal, na precedência do seletor; só os presentes |
| `actionableRegistrations` | Candidatas em unidades elegíveis |
| `heldRegistrations` | Candidatas em unidades pausadas ou grandes demais |
| `units` | `{eligible, blocked, oversized}` |
| `page[]` | Até 10 unidades / 100 inscrições: `{subjectUserId, originalMeasureId, measureVersion, items[], unitDigest}` |
| `page[].items[]` | `{eventId, registrationVersion, state, maxReleasedSeats, maxPromotions}` |
| `page[].unitDigest` | SHA-256 da unidade em hexadecimal minúsculo (64 caracteres), a informar na criação de um run ([API-ADM26](registration-reconciliation-runs.md)) |
| `next` | `{cursorSubject, cursorMeasure}` da próxima página, ou `null` |

As contagens cobrem toda a população num snapshot; cada página tem `asOf` próprio, e contagens de páginas
diferentes são indicativas. `maxPromotions` é teto condicional: fila, horários e elegibilidade podem mudar.

### `unitDigest`

SHA-256 do texto canônico v1 em UTF-8, com uma linha terminada em `\n` para cada campo, nesta ordem:

```text
duora.registration-reconciliation.unit-digest.v1
selectorVersion={versão do seletor}
evidenceId={id do descritor de corte}
lower={limite inferior, yyyy-MM-ddTHH:mm:ss.ffffffZ}
upper={limite superior, mesmo formato}
subjectUserId={uuid}
originalMeasureId={uuid}
measureVersion={versão da medida}
item={eventId};{registrationVersion};{state};{maxReleasedSeats};{maxPromotions}
```

Há uma linha `item` por inscrição, em ordem ordinal do `eventId`. UUIDs em minúsculas, com hífens. O digest prova
que o moderador viu a mesma unidade que o servidor recalcula; não é segredo nem assinatura
([ADR-0049](../architecture/adr/0049-execucao-da-reconciliacao-de-inscricoes-legadas.md), D1). Como inclui
`maxPromotions`, mudanças na fila de terceiros ou o início do evento também mudam o digest. Mudar a serialização
exige outra versão na primeira linha. `ReconciliationUnitDigestTests` fixa o valor literal.

## Corte configurado

Seção `RegistrationReconciliation:Cutover`, com `EvidenceId` (letras e dígitos ASCII, `-`, `_`, `.`, `:`; até
128), `Lower` e `Upper` (instantes UTC com sufixo `Z`, até seis casas decimais, `Upper >= Lower`). Ausente por padrão: tudo sai como
`MissingDeploymentEvidence` e nada é candidato. Presente e incompleta ou inválida: a Web não sobe
(`ValidateOnStart`). Nenhum corte real está cadastrado; preenchê-lo exige o descritor de implantação revisado da
seção 4 da proposta, nunca a data da migração.

## O que "sem escrita" cobre

Nenhuma mutação lógica do banco da aplicação durante a requisição: sessão, inscrições, eventos, contas, medidas,
guardas, auditoria, outbox, jobs, idempotência e sequências ficam iguais, e não há `Set-Cookie`. PostgreSQL e
infraestrutura ainda produzem estatísticas, logs e I/O físico; isso não é promessa de zero escrita em disco.
Não há auditoria persistida da leitura; se uma política vier a exigi-la, ela precisa ser conciliada antes de manter
a promessa.

## Uso e limites

- A prévia não conta como atividade da sessão: com 15 min sem outra rota a sessão administrativa expira.
- Não copiar a resposta para issue, PR, log público ou modelo: ela contém IDs de participantes.
- Rollback: remover a rota (ou só o metadado sem toque) em release revisada; não há dado a migrar.

Validação: `RegistrationReconciliationPreviewHttpTests`, `RegistrationReconciliationPreviewEffectsTests`,
`ReadOnlySessionTests`, `ReconciliationCutoverEvidenceTests` e
`StartupConfigurationTests.WebDoesNotStartWithAnIncompleteOrInvalidReconciliationCutover`, com dados sintéticos.
