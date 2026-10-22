# Revisão humana de fotos — contrato para implementação (#173 / M0)

Estado em 22/10/2026: **contrato documental consolidado**, baseado no ADR-0035 aceito pelo #178.
Esta entrega não altera endpoints, DTOs, OpenAPI, schema, políticas executáveis, allowlist ou configuração.
Os nomes de rotas/campos abaixo definem o alvo das entregas M1–M7; não são funcionalidades disponíveis.
A revisão humana da evolução de contrato/autorização permanece gate antes de código executável.

## Inventário verificado

| Operação | Código atual | Entrega posterior |
|---|---|---|
| API-M01 POST `/api/v1/me/photos` | PhotoEndpoints/PhotoUploads: corpo cru, idempotência, 202 e photoId/status; runtime padrão recusa uploads | Acrescentar HumanReview e replay de AwaitingReview após leitores compatíveis |
| API-M02 GET `/api/v1/me/photos/{photoId}/status` | Titular recebe photoId/status/rejectionCode; seis estados existentes | Acrescentar estado editorial e assetId opcional para derivado aprovado |
| API-M03 DELETE `/api/v1/me/photos/{photoId}` | Não mapeado em PhotoEndpoints | Exclusão lógica atômica, tombstone e limpeza durável; autorização do titular |
| API-M04 GET `/api/v1/media/{assetId}` | Não mapeado no fluxo atual | Leitura autorizada de derivado aprovado pela API, nunca original/URL de storage |
| Fila, evidência, decisão e recurso | Safety não persiste casos editoriais; não há painel | Persistência/contratos/rotas administrativas abaixo |
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
| Decisão humana de reprovar | Rejected | PHOTO_REVIEW_REJECTED | Motivo interno no caso; recurso autorizado, sem sanção da conta |
| Sem decisão até 72 h da entrada em revisão | Rejected | PHOTO_REVIEW_EXPIRED | Limpeza do candidato em até 24 h; não aprovar automaticamente |
| Candidato irrecuperavelmente ausente, inclusive após restore | Rejected | PHOTO_REVIEW_UNAVAILABLE | Novo envio, nunca reconstrução/aprovação a partir de metadados |
| Exclusão do titular | Deleted | null | Revogar acesso, persistir tombstone/limpeza; preservação só do recorte autorizado |
| Rejeição técnica | Rejected | Código técnico atual | Preservar semântica; não disfarçar decisão humana com PROCESSING_LIMIT |

Recurso não faz Rejected → Approved cego: abre nova revisão ligada à decisão humana. Se aprovada, exige
os mesmos checks de candidato/geração/conta/tombstone e commit condicional da aprovação inicial.
Pending/AwaitingReview/erros não criam asset público nem confirmação de identidade/idade.

### API-M01/M02: evolução do status

API-M01 mantém request/headers/202/Location atuais; replay devolve a mesma photoId e o estado atual.
Idempotency-Key igual com bytes diferentes continua 409. A negociação ADR-0034 continua restrita aos campos
já definidos de validação de upload; **PHOTO_REVIEW_* são resultados do processamento, fora de errors**.

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
| PHOTO_REVIEW_REJECTED | A foto não foi aprovada. Consulte as regras para enviar outra foto ou solicitar revisão da decisão dentro do prazo. | This photo was not approved. Check the rules to upload another photo or request a review of the decision within the deadline. |
| PHOTO_REVIEW_EXPIRED | A análise não terminou no prazo. Envie outra foto para tentar novamente. | The review did not finish before the deadline. Upload another photo to try again. |
| PHOTO_REVIEW_UNAVAILABLE | Esta foto não está mais disponível para análise. Envie outra foto. | This photo is no longer available for review. Upload another photo. |

A interface apresenta a janela efetiva de recurso quando a decisão for recorrível e houver candidato retido;
não promete restaurar foto ausente nem inventa um motivo editorial específico. Motivos internos não podem
ser inferidos de status, mensagens, URL ou headers.

## Contratos HTTP futuros

Prefixos `/api/v1`; respostas privadas têm `Cache-Control: no-store`. Mutações usam antiforgery existente,
JSON estrito, `Idempotency-Key` e versão esperada. A identidade vem da sessão, nunca do corpo.
Listas são paginadas por cursor opaco; 50 itens por padrão, no máximo 100, sem contagem global ou thumbnails.
Esses tetos de paginação são detalhes de contrato, não aumento da cota de fotos 3/100.

| Método/rota | Entrada mínima / sucesso | Autorização e projeção |
|---|---|---|
| GET `/admin/photo-reviews?cursor=...&limit=...` | 200: id opaco do caso, state, version, datas necessárias e nextCursor | Sessão administrativa/MFA, moderator + permissão explícita; por padrão só casos atribuídos ao chamador |
| POST `/admin/photo-reviews/{caseId}/assignments` | reviewerId, If-Match, Idempotency-Key → 200 e nova versão | moderator com capacidade explícita de atribuição; alvo precisa estar habilitado para revisar; registro auditado. Capacidade de atribuir não concede leitura de fotos |
| GET `/admin/photo-reviews/{caseId}` | 200: caso autorizado, política/versionamento e histórico mínimo | Caso atribuído; dados pessoais mínimos, sem path/URL de storage; ETag forte |
| GET `/admin/photo-reviews/{caseId}/evidence/{variant}` | variant em thumb/card/full → 200 image/webp | Caso atribuído e candidato disponível; auditar leitura **antes** de qualquer byte; sem original, nome de arquivo do usuário ou redirecionamento |
| POST `/admin/photo-reviews/{caseId}/decisions` | decision approve/reject, reasonCode fechado, If-Match, Idempotency-Key → 200 e versão/resultado | Caso atribuído + MFA/step-up; revalidar conta/guardas/geração e papel no commit. Texto livre/motivo sensível não entra no log |
| POST `/me/photos/{photoId}/appeals` | Pedido de revisão da própria decisão, If-Match, Idempotency-Key → 202 e protocolo mínimo | Titular, decisão humana recorrível e prazo aberto; não é recurso de sanção API-S06; não aceita foto arbitrária como evidência |
| GET `/me/photos/{photoId}/appeals/{appealId}` | 200: protocolo/status/datas necessários, sem revisor | Titular; isolamento entre contas e id inexistente uniformes |
| DELETE `/me/photos/{photoId}` | If-Match, Idempotency-Key → 204 | Titular. Deleted próprio repetido mantém 204 enquanto registro existir; alheio/inexistente dá mesmo404 |
| GET `/media/{assetId}?variant=...` | Variante em thumb/card/full → 200 image/webp, default full | Asset Approved não basta: titular ou contexto de perfil/sala explicitamente permitido, elegibilidade e Safety revalidados antes dos bytes; alheio/inexistente/não aprovado dá mesmo404 |

GET de evidência e GET público de asset são operações diferentes. moderator/product_admin/operator não
recebem leitura de fotos por serem privilegiados. Atribuição, leitura, decisão, recurso e preservação são
capacidades explícitas; recurso exige revisor diferente de quem tomou a decisão anterior. A ausência de
nomes/cobertura continua gate de ativação; não preencher responsáveis a partir do titular de suporte/região.

ETag do caso e do pedido de recurso é forte, opaco e versionado pelo servidor; não reutilizar ETag de perfil.
Mutações de caso e exclusão usam a versão do objeto que alteram, não versão enviada de outro recurso.
Sem If-Match:428; formato inválido:400; versão vencida:412, sem efeito. Repetição da mesma chave/corpo retorna
resultado já confirmado, mesmo que sua versão anterior já tenha avançado; chave com intenção diferente:409.
Revisão/exclusão concorrentes revalidam guardas e versão no commit; somente a intenção vencedora produz efeito.

Sessão ausente:401; papel/capacidade ausente antes de localizar caso:403; caso/foto fora do escopo:404 uniforme;
estado incompatível/recurso duplicado divergente:409; corpo inválido:400; limitação:429 + Retry-After;
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

## Relógio, recurso, retenção e perda de bytes

Todos os instantes são UTC persistidos; um limite vence quando `now >= deadline` (intervalo fechado no início,
aberto no fim). Não usar relógio do navegador. Limites são avaliados na transação do efeito e rechecados após
espera por guarda. Timers atrasados não ampliam autorização nem tornam candidato publicável.

| Marco | Referência / ação |
|---|---|
| Primeira análise alvo24h | awaitingReviewAt +24h; métrica/alerta, sem aprovação automática |
| Espera máxima72h | awaitingReviewAt +72h; sem decisão confirmada, PHOTO_REVIEW_EXPIRED; limpeza até +24h |
| Solicitar recurso até7dias | rejectedAt +7dias, da decisão humana; prazo/candidato checados atomicamente com retenção |
| Alvo da decisão do recurso7dias | appealRequestedAt +7dias; atraso exige escalonamento, sem manter bytes além do teto nem deferir automaticamente |
| Candidato reprovado sem recurso | rejectedAt +7dias; fila de limpeza não impede recurso recebido validamente antes do limite |
| Com recurso aberto | até closedAt +24h, limitado a rejectedAt +30dias, salvo preservação excepcional explícita |
| Registro mínimo da decisão/auditoria | 1ano conforme ADR-0035; sem imagem ou texto sensível |

Um recurso recebido validamente precisa estender retention_until e criar o caso na mesma transação que
impede a limpeza. Decisão/recurso empatados com a limpeza são serializados; não ressuscitar bytes apagados.
Vencimento do alvo de7dias é incidente operacional, não julgamento. Se chegar ao teto de30dias sem solução,
revogar e limpar candidato, encerrar a capacidade de revisão por falta de evidência e registrar o não julgamento;
a reprovação original não se transforma em nova decisão de mérito. Status público continua Rejected, sem
sanção extra e com orientação para novo envio. Se candidato desaparecer antes, usar PHOTO_REVIEW_UNAVAILABLE;
não chamar indisponibilidade de decisão humana. Expiração/perda sem decisão não inventa um julgamento recorrível.

Preservação excepcional exige referência/escopo/motivo/ator/vencimento explícitos e auditoria, segundo
retention.md. O recurso por si só não cria hold ilimitado; este contrato não autoriza ninguém a criá-lo.
Exclusão do titular revoga acesso e fecha o processamento; só o recorte de evidência com hold válido é
preservado em área separada autorizada. Foto retida como evidência não reaparece no perfil ou no recurso.

review-candidates contém apenas derivados sanitizados, fora do backup de mídia. Casos/metadados continuam no
backup do banco. Restore aplica tombstones e reconcilia retenção/autorizações antes de admitir leitura ou jobs;
candidato ausente resulta em PHOTO_REVIEW_UNAVAILABLE e novo envio. Não prometer exclusão imediata de backups
imutáveis nem reter original para reconstruir revisão. Jobs/campos reais serão implementados em M2/M6.

## Compatibilidade e gates de implementação

- M1: leitores, DTOs/types/OpenAPI, CHECK/índices/cotas e consumidores antes de escritores. AwaitingReview conta
  nas cotas3/100 inclusive na consulta PhotoUploads.AwaitingVerdict; não basta mudar PendingStates.
- M2/M3: arquivo privado/caso e transações, geração, idempotência, leitura auditada e decisão/recurso.
- M4/M5: HTTP/UI bilíngue, autorização e API-M03/M04/perfil. Nenhum assetId ou nova rota é emitido agora.
- M6: retenção, órfãos, tombstones, recurso sem evidência e restore. Prazos aprovados não dispensam testes.
- M7: domínio/Web/Worker/allowlist e montagens coerentes depois do fluxo aceito, mantendo inventário padrão vazio.
- Rollback só com leitores compatíveis e versão segura. Desligar upload não torna binário antigo capaz de ler
  AwaitingReview; não apagar escolhas/decisões ou reclassificá-las como Approved para caber no binário anterior.

## Matriz de aceite futuro

| Contrato | Invariantes/aceite | Evidência exigida nas próximas entregas |
|---|---|---|
| Estados/replay/códigos e readers-before-writers | INV-05/06; T-05/13 | HTTP real, replay após transições, cliente desconhecido, schema/constraints e rollback de leitores |
| Atribuição/leitura/decisão/recurso | INV-03/05/08/10; T-01/03/10 | Negativos por conta/role/atribuição/MFA, auditoria indisponível sem byte, decisões/revogação concorrentes |
| Caso/candidato/promoção | INV-06/11; T-05/06/08/16 | PostgreSQL real, falhas entre arquivo/commit, lease vencido, repetição/geração e reinício sem asset parcial |
| Exclusão/retenção/restore | INV-05/06/11; T-08/26 | Limites exatos de relógio, recurso/cleanup simultâneos, tombstone/hold mínimo, restore sem candidato |
| Perfil/asset aprovado | INV-03/05/10; T-01/03/13 | Contexto de leitura revalidado, bloqueio/restrição, no-store, ausência de original/path/URL |

Esta tabela exige testes futuros; não os declara implementados. #173/#174/#86/#69 continuam abertas.
Sem equipe e homologação adequadas, não ativar HumanReview/upload. Detector/filtro regional continuam fora do escopo.
