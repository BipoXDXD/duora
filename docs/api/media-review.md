# Revisão humana de fotos — contrato para implementação (#173 / M0)

Estado em 22/10/2026: **contrato documental consolidado**, baseado no ADR-0035 aceito pelo #178.
Esta entrega não altera endpoints, DTOs, OpenAPI, schema, políticas executáveis, allowlist ou configuração.
Os nomes de rotas/campos abaixo definem o alvo das entregas M1–M7; não são funcionalidades disponíveis.
A revisão humana da evolução de contrato/autorização permanece gate antes de código executável.

**Decisões de 28/10/2026** ([ADR-0035](../architecture/adr/0035-revisao-humana-de-fotos.md#decisões-de-28102026)):
HumanReview é admitida em Production (o upload continua desligado até a #69); a revisão de fotos **não tem
recurso** e tem um único revisor, o dono do projeto; foto recusada se resolve com novo envio, que abre revisão
nova. Os trechos de recurso abaixo foram removidos ou marcados; a [matriz de autorização](#matriz-de-autorização-28102026)
descreve as rotas que restaram.

## Inventário verificado

| Operação | Código atual | Entrega posterior |
|---|---|---|
| API-M01 POST `/api/v1/me/photos` | PhotoEndpoints/PhotoUploads: corpo cru, idempotência, 202 e photoId/status; runtime padrão recusa uploads | Acrescentar HumanReview e replay de AwaitingReview após leitores compatíveis |
| API-M02 GET `/api/v1/me/photos/{photoId}/status` | Titular recebe photoId/status/rejectionCode; seis estados existentes | Acrescentar estado editorial e assetId opcional para derivado aprovado |
| API-M03 DELETE `/api/v1/me/photos/{photoId}` | Recorte executável (ver "Entrega API-M03/M04"): todos os estados menos Pending, tombstone no banco e coletores físicos | Ledger externo de tombstones (sem custódia B2 até a #69) e retenção dos registros |
| API-M04 GET `/api/v1/media/{assetId}` | Recorte executável: só o titular, derivado de asset Approved, `no-store` | Contextos de perfil/sala quando a seleção de foto existir (API-P04) |
| Fila, evidência e decisão | Safety não persiste casos editoriais; não há painel | Persistência/contratos/rotas administrativas abaixo (recurso removido em 28/10/2026) |
| T-26 / exclusão e restore editorial | Não atendido por upload, outbox ou retenção operacional genérica | Testes de exclusão, preservação limitada, órfãos, backups/tombstones e candidato ausente |

Perfil/seleção de foto: publicar asset não seleciona implicitamente foto do perfil. Toda referência de perfil
precisa pertencer à conta, apontar a asset Approved e ser revalidada no salvamento/leitura sob as guardas
pertinentes. A implementação do comando de seleção e das projeções de perfil deve integrar o recorte M5;
não expor candidato por reaproveitar a rota de perfil. Nenhuma rota de perfil é acrescentada por M0.

## Estados públicos e resultado

Preservar Pending → Quarantined → Processing e estados Approved, Rejected e Deleted.
HumanReview acrescenta **AwaitingReview** entre Processing e resultado humano. É um valor público real,
inclusive no replay de API-M01; não o projetar como Processing. Nenhum dado antigo recebe esse estado por seed.

| Evento | Estado público | rejectionCode | Efeito permitido |
|---|---|---|---|
| Sanitização técnica válida com HumanReview | AwaitingReview | null | Candidato privado + caso; encerrar entrega técnica, apagar original duravelmente |
| Decisão humana de aprovar + promoção/commit válidos | Approved | null | Asset/variantes finais + decisão/auditoria/outbox atômicos |
| Decisão humana de reprovar | Rejected | PHOTO_REVIEW_REJECTED | Motivo interno no caso; sem recurso nem sanção da conta; limpeza do candidato em até 24 h; novo envio abre revisão nova |
| Sem decisão até 72 h da entrada em revisão | Rejected | PHOTO_REVIEW_EXPIRED | Limpeza do candidato em até 24 h; não aprovar automaticamente |
| Candidato irrecuperavelmente ausente, inclusive após restore | Rejected | PHOTO_REVIEW_UNAVAILABLE | Novo envio, nunca reconstrução/aprovação a partir de metadados |
| Exclusão do titular | Deleted | null | Revogar acesso, persistir tombstone/limpeza; preservação só do recorte autorizado |
| Rejeição técnica | Rejected | Código técnico atual | Preservar semântica; não disfarçar decisão humana com PROCESSING_LIMIT |

Não há recurso de fotos (28/10/2026): nada faz Rejected → Approved. A pessoa envia uma foto nova, que é outro
upload com outro caso, sujeito às cotas 3/100 e aos rate limits existentes.
Pending/AwaitingReview/erros não criam asset público nem confirmação de identidade/idade.

### API-M01/M02: evolução do status

API-M01 mantém request/headers/202/Location atuais; replay devolve a mesma photoId e o estado atual.
Idempotency-Key igual com bytes diferentes continua 409. A negociação ADR-0034 continua restrita aos campos
já definidos de validação de upload; **PHOTO_REVIEW_* são resultados do processamento, fora de errors**.

API-M02 futura responde também com **ETag forte e opaco da versão do upload/foto**, inclusive para Rejected
e Deleted enquanto o registro existir. Esse é o valor de If-Match para excluir a foto;
não usar versão do perfil nem do caso. API-M01/replay pode devolver o mesmo
ETag da foto, mas API-M02 é a fonte obrigatória para reler a versão após conflito.

API-M02 futura mantém os campos atuais e acrescenta `assetId` nullable: só tem valor se status Approved e
existir asset final autorizado. Caso, revisor, evidência, motivo interno, chaves de arquivo e URLs não aparecem.

```json
{"photoId":"00000000-0000-4000-8000-000000000001","status":"AwaitingReview","rejectionCode":null,"assetId":null}
```

```json
{"photoId":"00000000-0000-4000-8000-000000000001","status":"Rejected","rejectionCode":"PHOTO_REVIEW_REJECTED","assetId":null}
```

Estado/código desconhecidos no cliente falham para uma apresentação local segura; não mostrar texto
arbitrário da API, não interpretar desconhecido como Approved nem repetir POST para negociar formato.
Leitores, constraints, índices de cota e consumidores devem estar preparados antes de qualquer escritor.
Não gerar OpenAPI/tipos deste alvo enquanto não houver entrega executável revisada de M1.

### Mensagens públicas PT-BR/EN-US

| Código | PT-BR | EN-US |
|---|---|---|
| PHOTO_REVIEW_REJECTED | A foto não foi aprovada. Consulte as regras e envie outra foto. | This photo was not approved. Check the rules and upload another photo. |
| PHOTO_REVIEW_EXPIRED | A análise não terminou no prazo. Envie outra foto para tentar novamente. | The review did not finish before the deadline. Upload another photo to try again. |
| PHOTO_REVIEW_UNAVAILABLE | Esta foto não está mais disponível para análise. Envie outra foto. | This photo is no longer available for review. Upload another photo. |

Sem recurso de fotos, a interface orienta novo envio e não oferece pedido de revisão da decisão;
não promete restaurar foto ausente nem inventa um motivo editorial específico. Motivos internos não podem
ser inferidos de status, mensagens, URL ou headers.

## Contratos HTTP futuros

Prefixos `/api/v1`; respostas privadas têm `Cache-Control: no-store`. As novas mutações usam antiforgery
existente, `Idempotency-Key` e versão esperada. Operações com JSON recusam campos desconhecidos; DELETE
não recebe corpo, e API-M01 preserva o corpo binário atual. A identidade vem da sessão, nunca do corpo.
Listas são paginadas por cursor opaco; 50 itens por padrão, no máximo 100, sem contagem global ou thumbnails.
Esses tetos de paginação são detalhes de contrato, não aumento da cota de fotos 3/100.

| Método/rota | Entrada mínima / sucesso | Autorização e projeção |
|---|---|---|
| GET `/admin/photo-reviews?cursor=...&limit=...` | 200: id opaco do caso, state, version, datas necessárias e nextCursor | Sessão administrativa/MFA, moderator + permissão explícita; por padrão só casos atribuídos ao chamador |
| POST `/admin/photo-reviews/{caseId}/assignments` | reviewerId, If-Match, Idempotency-Key → 200 e nova versão | moderator com capacidade explícita de atribuição; alvo precisa estar habilitado para revisar; registro auditado. Capacidade de atribuir não concede leitura de fotos |
| GET `/admin/photo-reviews/{caseId}` | 200: caso autorizado, política/versionamento e histórico mínimo | Caso atribuído; dados pessoais mínimos, sem path/URL de storage; ETag forte |
| GET `/admin/photo-reviews/{caseId}/evidence/{variant}` | variant em thumb/card/full → 200 image/webp | Caso atribuído e candidato disponível; auditar leitura **antes** de qualquer byte; sem original, nome de arquivo do usuário ou redirecionamento |
| POST `/admin/photo-reviews/{caseId}/decisions` | decision approve/reject, reasonCode fechado, If-Match, Idempotency-Key → 200 e versão/resultado | Caso atribuído + MFA/step-up; revalidar conta/guardas/geração e papel no commit. Texto livre/motivo sensível não entra no log |
| DELETE `/me/photos/{photoId}` | If-Match, Idempotency-Key → 204 | Titular. Deleted próprio repetido mantém 204 enquanto registro existir; alheio/inexistente dá mesmo404 |
| GET `/media/{assetId}?variant=...` | Variante em thumb/card/full → 200 image/webp, default full | Asset Approved não basta: titular ou contexto de perfil/sala explicitamente permitido, elegibilidade e Safety revalidados antes dos bytes; alheio/inexistente/não aprovado dá mesmo404 |

As rotas de recurso de fotos (`/me/photos/{photoId}/appeals` e o grupo `/admin/photo-appeals`) foram removidas
em 28/10/2026: nenhuma rota registrada contém `appeal`, e o POST do titular dá 404. O recurso de sanção API-S06
é outra feature e não muda.

GET de evidência e GET público de asset são operações diferentes. moderator/product_admin/operator não
recebem leitura de fotos por serem privilegiados. Atribuição, leitura e decisão são capacidades explícitas
(`assign`, `evidence`, `decide`), concedidas só ao papel `moderator`. Desde 28/10/2026 há um único revisor, o
dono do projeto, sem substituto nem revisor independente; com ele indisponível, suspendem-se novas admissões
pelo procedimento aprovado.

ETag do caso vem do GET administrativo do caso e é consumido pelas mutações administrativas. DELETE consome
o ETag da foto obtido em API-M02. Não reutilizar tags entre tipos de recurso HTTP (foto, caso) ou de perfil.
If-Match deve conter uma única tag forte: não aceitar wildcard, tag fraca ou lista para contornar a versão.
Sem If-Match:428; formato inválido:400; versão vencida:412, sem efeito. Repetição da mesma chave/corpo retorna
resultado já confirmado, mesmo que sua versão anterior já tenha avançado; chave com intenção diferente:409.
Revisão/exclusão concorrentes revalidam guardas e versão no commit; somente a intenção vencedora produz efeito.
Autenticação/CSRF e presença/sintaxe dos headers continuam obrigatórias no replay. Depois dessa validação,
a mesma chave/intenção confirmada retorna o resultado salvo antes de comparar a versão atual. DELETE204
devolve ETag da foto após o efeito; Deleted próprio com chave nova só retorna204 se If-Match atual for
válido. Tag vencida dá412 e ausência dá428 também nessa situação; após expurgo da linha vale404 uniforme.

Sessão ausente:401; papel/capacidade ausente antes de localizar caso:403; caso/foto fora do escopo:404 uniforme;
estado incompatível:409; corpo inválido:400; limitação:429 + Retry-After;
auditoria/storage/infra transitória:503 + Retry-After. Sem criar enum permissivo por typo.
O catálogo de Problem Details será atualizado pela entrega executável: os textos acima não viram detail
arbitrário nem novo código publicado por M0. Falha de cópia/auditoria/commit não confirma decisão aplicada.

## Atomicidade, I/O e divulgação

1. Leitura da evidência: autorizar e auditar antes dos bytes, falhando fechada se auditoria falhar. Revalidar
   geração/candidato/conta na abertura do stream; cancelar transmissão quando a revogação for observável.
2. Sanitização: arquivo candidato fica privado; transação curta faz AwaitingReview + caso Safety + auditoria/
   outbox necessários. Nenhum lease fica esperando pessoa. Original é removido após derivação/commit com retry.
3. Aprovação: preparar cópia fora dos locks; commit condicional confirma asset/upload/decisão/auditoria/efeitos.
   O arquivo copiado não fica público sem commit. Idempotência e geração impedem apagar/sobrescrever vencedor.
4. Reprovação/expiração/exclusão: negar leitura imediatamente no estado persistido; limpeza física é durável,
   limitada e idempotente. Bloqueio/restrição/exclusão concorrente vence permissões obsoletas.
5. Ordem global de locks continua a de modules.md; sem storage, rede, decoder ou humano sob locks de negócio.
   Permissão/atribuição/versão não são guardadas apenas em cache para uma decisão posterior.

## Relógio, retenção e perda de bytes

Todos os instantes são UTC persistidos; um limite vence quando `now >= deadline` (intervalo fechado no início,
aberto no fim). Não usar relógio do navegador. Limites são avaliados na transação do efeito e rechecados após
espera por guarda. Timers atrasados não ampliam autorização nem tornam candidato publicável.

| Marco | Referência / ação |
|---|---|
| Primeira análise alvo24h | awaitingReviewAt +24h; métrica/alerta, sem aprovação automática |
| Espera máxima72h | awaitingReviewAt +72h; sem decisão confirmada, PHOTO_REVIEW_EXPIRED; limpeza até +24h |
| Candidato aprovado ou reprovado | decidedAt +24h; a decisão grava a obrigação de limpeza e o Worker a executa logo após (`DecidedCandidateCleanupDispatcher`). Até 28/10/2026 o reprovado ficava 7dias à espera de recurso |
| Registro mínimo da decisão/auditoria | 1ano conforme ADR-0035; sem imagem ou texto sensível |

Decisão empatada com a limpeza é serializada; não ressuscitar bytes apagados. Status público de rejeição
continua Rejected, sem sanção extra e com orientação para novo envio. Se candidato desaparecer antes da
decisão, usar PHOTO_REVIEW_UNAVAILABLE; não chamar indisponibilidade de decisão humana.

Preservação excepcional exige referência/escopo/motivo/ator/vencimento explícitos e auditoria, segundo
retention.md; este contrato não autoriza ninguém a criá-la.
Exclusão do titular revoga acesso e fecha o processamento; só o recorte de evidência com hold válido é
preservado em área separada autorizada. Foto retida como evidência não reaparece no perfil.

review-candidates contém apenas derivados sanitizados, fora do backup de mídia. Casos/metadados continuam no
backup do banco. Restore aplica tombstones e reconcilia retenção/autorizações antes de admitir leitura ou jobs;
candidato ausente resulta em PHOTO_REVIEW_UNAVAILABLE e novo envio. Não prometer exclusão imediata de backups
imutáveis nem reter original para reconstruir revisão. Jobs/campos reais serão implementados em M2/M6.

## Compatibilidade e gates de implementação

- M1: leitores, DTOs/types/OpenAPI, CHECK/índices/cotas e consumidores antes de escritores. AwaitingReview conta
  nas cotas3/100 inclusive na consulta PhotoUploads.AwaitingVerdict; não basta mudar PendingStates.
- M2/M3: arquivo privado/caso e transações, geração, idempotência, leitura auditada e decisão (recurso removido em 28/10/2026).
- M4/M5: HTTP/UI bilíngue, autorização e API-M03/M04/perfil. Nenhum assetId ou nova rota é emitido agora.
- M6: retenção, órfãos, tombstones e restore. Prazos aprovados não dispensam testes.
- M7: domínio/Web/Worker/allowlist e montagens coerentes depois do fluxo aceito, mantendo inventário padrão vazio.
- Rollback só com leitores compatíveis e versão segura. Desligar upload não torna binário antigo capaz de ler
  AwaitingReview; não apagar escolhas/decisões ou reclassificá-las como Approved para caber no binário anterior.

## Matriz de aceite futuro

| Contrato | Invariantes/aceite | Evidência exigida nas próximas entregas |
|---|---|---|
| Estados/replay/códigos e readers-before-writers | INV-05/06; T-05/13 | HTTP real, replay após transições, cliente desconhecido, schema/constraints e rollback de leitores |
| Atribuição/leitura/decisão | INV-03/05/08; T-01/03 | Negativos por conta/role/atribuição/MFA, auditoria indisponível sem byte, decisões/revogação concorrentes |
| Caso/candidato/promoção | INV-06/11; T-05/06/08/16 | PostgreSQL real, falhas entre arquivo/commit, lease vencido, repetição/geração e reinício sem asset parcial |
| Exclusão/retenção/restore | INV-05/06/11; T-08/26 | Limites exatos de relógio, decisão/cleanup simultâneos, tombstone/hold mínimo, restore sem candidato |
| Perfil/asset aprovado | INV-03/05/10; T-01/03/13 | Contexto de leitura revalidado, bloqueio/restrição, no-store, ausência de original/path/URL |

Esta tabela exige testes futuros; não os declara implementados. #173/#174/#86/#69 continuam abertas.
HumanReview é admitida em Production desde 28/10/2026, mas ligar o upload continua dependendo da #69 e de homologação. Detector/filtro regional continuam fora do escopo.

## Preparação executável M1

A entrega M1 prepara leitura/status/replay de AwaitingReview e dos três resultados editoriais, constraints,
índice/cotas e raiz SQL do caso Safety. Nenhum escritor HumanReview existe nessa etapa. As afirmações de
M0 acima continuam delimitando o contrato futuro: assetId, ETag e rotas administrativas são posteriores.
DTOs/OpenAPI/tipos atuais conservam strings e o formato existente; o gate contract regenera/compara os artefatos.
A SPA tem transporte genérico testado, ainda sem tela consumidora de status.
[Relatório M1 e gate de rollback antes dos escritores](../testing/reports/media-review-readers.md).

## Entrega API-M03/M04 — recorte executável (#173)

Atrás do mesmo gate do upload: sem `Media:Review`, as duas rotas respondem 503 `MEDIA_UPLOADS_DISABLED`;
Production admite HumanReview desde 28/10/2026, mas o inventário mantém `duora_media_review` vazio, então nada muda lá até a #69. Com o upload ligado, a Web exige `Media:Deletion`
(escopo e material da chave de operação V1) no boot.

**API-M03.** API-M02 passa a devolver o ETag forte `"photo-<id>-<versão>"`, o mesmo da linha do tempo de revisão.
DELETE sem corpo, com If-Match exato (428 ausente, 412 qualquer outra forma ou versão vencida) e Idempotency-Key
(400 inválida). A chave de operação e a intenção vêm de `PhotoDeletionIntentBinding`: o recibo é procurado pela
chave antes de comparar a versão atual, e a intenção recalculada com o snapshot gravado distingue repetição (204
com o mesmo ETag) de reuso para outra foto ou versão (409). Uma transação curta, depois da guarda Safety do dono e
da sessão, trava a foto e o asset e grava estado `Deleted`, tombstone, recibo, obrigações de limpeza das chaves
exatas e a auditoria `media.photo_deleted`. Alheio e inexistente dão o mesmo 404. Foto já `Deleted` com If-Match
atual e chave nova dá 204 sem novo efeito. O coletor do Worker apaga um objeto por lease, fora de transação, com
backoff, e só chaves do original ou de derivados do asset excluído dessa foto.

Estados (28/10/2026): `Quarantined`, `Processing`, `AwaitingReview`, `Approved` e `Rejected` são excluídos; só
`Pending` (o original ainda chegando, dono da recepção) responde 409 `PHOTO_DELETE_STATE_CONFLICT`. Na mesma
transação:

- candidato privado confirmado da revisão recebe a obrigação de limpeza explicada pelo tombstone (um marcador
  anterior de expiração, indisponibilidade ou decisão é mantido e assumido), consumida pelo
  `DecidedCandidateCleanupDispatcher`;
- foto excluída em `Quarantined`/`Processing` sem asset ganha obrigações para os três derivados determinísticos
  do TechnicalOnly, disponíveis só depois de 15 min (`InFlightWriterGrace`), para um Worker que já passou da
  checagem de versão terminar de gravar (não há coletor de órfãos: uma gravação que termine depois da folga
  deixa o derivado sem obrigação; risco só do TechnicalOnly, restrito a Development/Testing);
- candidato em preparo e promoção da aprovação ainda não confirmados ficam com os coletores de preparo, que não
  dependem do estado da foto.

O caso editorial não é apagado: some da fila e da evidência do moderador, e decisão ou aprovação posteriores dão
404 sem publicar nada. O Worker que chega depois encontra `Deleted` e só encerra a entrega. O tombstone só existe
no banco: o 204 não espera ledger externo, e um restore anterior à exclusão ainda a desfaz (ver "Ledger externo"
abaixo). Retenção das linhas de tombstone e recibo continua aberta.

**Ledger externo (28/10/2026).** Não é viável sem custódia nova: para sobreviver a um restore do banco, o ledger
precisa ficar fora dele, num bucket B2 com credencial e retenção próprias. Essa custódia não existe e a
configuração de B2, backup e restore é da #69. O código de seleção do restore já exige a barreira autenticada do
ledger (`ApprovedMediaRestoreSelection`, `DeletionLedgerBarrier`), então um restore sem ela falha fechado em vez de
republicar foto excluída, e o passo 6 do runbook de restore ([backup-restore.md](../operations/backup-restore.md))
exige reaplicar o registro externo de tombstones, falhando fechado enquanto ele não existir.

**API-M04.** Só o titular lê, porque ainda não há seleção de foto de perfil nem contexto de sala. A autorização é
relida no banco a cada pedido, antes de abrir o objeto e de novo antes do primeiro byte; asset e upload precisam
estar `Approved`, sem tombstone. Resposta `image/webp`, `Cache-Control: no-store`, `nosniff`, sem range nem ETag.
Variante `thumb`, `card` ou `full` (padrão); qualquer outro valor é 400. Alheio, inexistente, não aprovado,
excluído ou arquivo ausente dão o mesmo 404.


## Decisões de 28/10/2026

Registradas no [ADR-0035](../architecture/adr/0035-revisao-humana-de-fotos.md#decisões-de-28102026). Para este
contrato: HumanReview admitida em Development, Testing e Production (Staging recusado); upload ainda desligado
em Production (`duora_media_review` vazio, #69); sem recurso de fotos; um único revisor, o dono do projeto, como
`moderator` com as capacidades `assign`, `evidence` e `decide`; a rejeição grava a limpeza do candidato em 24 h.
Continuam abertos: ledger externo (sem custódia B2 até a #69) e retenção dos registros de exclusão, API-P04 (foto no perfil ou na sala; `GET /profiles/{userId}` não existe), aceite da política de
privacidade pelo titular e ativação do upload (#69).

### Matriz de autorização (28/10/2026)

Rotas sob `/api/v1`, conferidas em `PhotoReviewEndpoints.cs`, `PhotoEndpoints.cs` e `MediaAssetEndpoints.cs`.
A matriz supõe o upload ligado (`Media:Review=HumanReview`). Com ele desligado, que é o estado de Production
hoje, toda rota abaixo responde 503 `MEDIA_UPLOADS_DISABLED` a quem passa da autenticação e da política do
grupo (anônimo continua 401; conta privilegiada em rota de participante e participante em `/admin` continuam
403). "Moderador" é o dono do projeto: papel `moderator`, sessão administrativa com MFA e as três capacidades
concedidas. "404 uniforme" quer dizer que alheio e inexistente dão o mesmo status e o mesmo corpo.

| Rota | Titular participante | Outro participante | Moderador (dono, com grants) | `product_admin` / `operator` | Anônimo |
|---|---|---|---|---|---|
| GET `/me/photos` (lista própria) | 200, só as próprias fotos | 200, só as fotos dele; nunca as do titular | 403 `FORBIDDEN` (política padrão: conta privilegiada não usa rota de participante) | 403 `FORBIDDEN` (idem) | 401 |
| GET `/me/photos/{photoId}/review-history` | 200 | 404 uniforme | 403 `FORBIDDEN` (política padrão) | 403 `FORBIDDEN` (política padrão) | 401 (política padrão) |
| DELETE `/me/photos/{photoId}` | 204 em `Quarantined`, `Processing`, `AwaitingReview`, `Approved` ou `Rejected`; 409 `PHOTO_DELETE_STATE_CONFLICT` só em `Pending` | 404 uniforme, nada muda | 403 `FORBIDDEN` (política padrão) | 403 `FORBIDDEN` (política padrão) | 401 ou 403 (o teste aceita os dois; a foto fica intacta) |
| GET `/media/{assetId}` | 200 `image/webp`, `no-store`, só asset `Approved` | 404 uniforme (também para não aprovado, excluído ou sem arquivo) | 403 `FORBIDDEN` (política padrão) | 403 `FORBIDDEN` (política padrão) | 401 |
| GET `/admin/photo-reviews/capabilities` | 403 `FORBIDDEN` | 403 `FORBIDDEN` | 200 | 403 `PHOTO_REVIEW_FORBIDDEN` (sem capacidade) | 401 `UNAUTHENTICATED` |
| GET `/admin/photo-reviews` | 403 `FORBIDDEN` | 403 `FORBIDDEN` | 200, só casos atribuídos a ele | 403 `PHOTO_REVIEW_FORBIDDEN` | 401 `UNAUTHENTICATED` |
| GET `/admin/photo-reviews/{caseId}` | 403 `FORBIDDEN` | 403 `FORBIDDEN` | 200 se atribuído; 404 se inexistente ou não atribuído | 403 `PHOTO_REVIEW_FORBIDDEN` | 401 `UNAUTHENTICATED` |
| POST `/admin/photo-reviews/{caseId}/assignments` | 403 `FORBIDDEN` | 403 `FORBIDDEN` | 200; 404 caso inexistente; 412/428 versão; 409 estado ou chave | 403 `PHOTO_REVIEW_FORBIDDEN` | 401 `UNAUTHENTICATED` |
| GET `/admin/photo-reviews/{caseId}/evidence/{variant}` | 403 `FORBIDDEN` | 403 `FORBIDDEN` | 200 `image/webp`, `no-store`, auditada antes dos bytes; 404 caso não atribuído ou variante desconhecida; 503 storage | 403 `PHOTO_REVIEW_FORBIDDEN` | 401 `UNAUTHENTICATED` |
| POST `/admin/photo-reviews/{caseId}/decisions` | 403 `FORBIDDEN` | 403 `FORBIDDEN` | 200 com MFA recente (TOTP nos últimos 5 min); sem ela, 401 `REAUTHENTICATION_REQUIRED` | 401 `REAUTHENTICATION_REQUIRED` sem MFA recente; com ela, 403 `PHOTO_REVIEW_FORBIDDEN` | 401 `UNAUTHENTICATED` |

Evidência: `PhotoReviewHttpTests` (fluxo do moderador único, 404 uniforme no histórico, rotas de recurso
inexistentes), `PhotoReviewOwnerPhotoListTests` (lista só do titular, 401), `PhotoDeletionTests` e
`MediaAssetReadTests` (404 uniforme, 401), `AdminRouteTests` (401 anônimo, 403 participante, 503 com upload
desligado, 401 `REAUTHENTICATION_REQUIRED` sem MFA recente numa ação crítica), `SecondFactorHardeningTests`
(403 da conta privilegiada em API-M01/M02) e `PhotoReviewAccessTests` (capacidades explícitas; `operator`, `product_admin` e `participant` não
recebem grant). As células de `product_admin`/`operator` com upload ligado e as 403 da conta privilegiada em
histórico, lista, DELETE e `/media` vêm do código (política padrão e recusa do serviço), sem teste HTTP próprio
para cada célula.
