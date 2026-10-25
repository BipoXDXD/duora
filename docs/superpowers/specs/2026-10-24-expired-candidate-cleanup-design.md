# M6b2 — coleta de candidatos com expiração comprovada

Filho do #239/2814b6a, após a expiração lógica M6b1. Contratos ADR0035/P173-5 e API media-review: limpar candidatos privados até24h após a expiração. Este dispatcher consome somente a obrigação criada atomicamente pelo System. Não decide moderação, não publica foto e não matricula rejeições humanas, abandonados ou aprovações.

## Elegibilidade e preservação

Exigir candidato committed/writer-settled, cleanup_requested_at e cleanup_available_at devidos, deleted_atNULL; cleanup_audit_id deve apontar para fato imutável System/actor_idNULL/action safety.photo_review_expired/target_type photo_review_case, com mesmo caso e versão. Não exigir igualdade de occurred_at e cleanup_requested_at: AuditTrail lê o relógio depois do efeito; a FK/ação/caso/versão vinculam a obrigação. Exigir política human-review-v1, geração/upload/titular correspondentes, upload Rejected/PHOTO_REVIEW_EXPIRED e vínculo de versões produzido pela expiração. Revalidar no ACK. Prova ausente, marcador futuro ou qualquer divergência recusam coleta; não inferir obrigação por idade/Rejected ou varrer diretório.

Apagar somente as três ObjectKey.ForReviewCandidate da geração reclamada. Preservar originais, derivados finais, demais gerações, caso/upload/decisões e ops.audit_events. deleted_at e last_cleanup_at registram conclusão, mantendo FK e histórico. Não descartar obrigação após31dias ou muitas tentativas. Recurso/hold/exclusão futura exigirão coordenação explícita própria antes de alterar contexto.

## Concorrência e recuperação

Claim curto com FOR UPDATE SKIP LOCKED, tokenUUID novo e lease60s. Fechar transação/conexão antes do I/O. DELETE com orçamento15s e cancelamento vinculado. ACK só token/lease/contexto vigentes, nunca após lease vencido mesmo sem substituto. Falha parcial agenda backoff persistido sem deleted_at; cancelamento deixa lease recuperável; novo processo retoma três DELETE idempotentes. Nenhuma tentativa remove auditoria ou amplia privilégios. Reutilizar colunas/índices #239 e coletor Worker existente apenas após qualificação; scaffold RED não se registra.

## Aceite

PostgreSQL real e armazenamento de arquivos sintéticos na CI hospedada. Cobrir obrigação imediata e31dias atrasada; relógio avança entre efeito e audit em toda preparação expirada; prova ausente com marcador válido e ação/caso/versão/geração/marcador divergentes; outros estados; exclusividade de lease/SKIP LOCKED real e ACK antigo/expirado/contexto modificado; falha parcial/backoff; shutdown e retomada; prova de ausência de lock SQL durante DELETE; due-time e enabled=false. Comparar projeção SQL e arquivos nos negativos; histórico, upload/caso/assets/variantes e bytes de outra aprovação/original nos positivos. Nenhuma execução no Mac, VPS, backup ou dados reais. CI anterior não transfere resultado ao novo head; primeiro RED comportamental, depois produto, revisão e gates próprios.

Este recorte não habilita HumanReview nem fecha173/69. Recurso, tombstones, backup Approved-only, restauração sintética e integrações HTTP/UI seguem o plano principal. Nenhuma publicação/instalação ou operação de produção está incluída.
