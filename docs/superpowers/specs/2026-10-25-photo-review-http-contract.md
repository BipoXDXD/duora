# API privada de revisão e recurso — contrato para implementação conjunta

Fonte de partida: PR256/76450c38bb52fec1608fe7d4abee4329b0f593c8. Este contrato autoriza desenvolvimento; não admite HumanReview em produção nem cria permissões de pessoas reais. Implementação e qualificação hospedada próprias ainda são necessárias. DTOs em Duora.Contracts/Media/PhotoReviewContracts.cs. Prefixo comum `/api/v1`; JSON camelCase e problemas conforme o envelope existente. Frontend trata todos os ETags e cursores como opacos.

## Rotas e respostas normativas

| Método e caminho | Entrada | Sucesso |
| --- | --- | --- |
| GET `/me/photos/{photoId}/review-history` | sessão do titular | 200 PhotoReviewHistoryResponse + ETag da foto |
| POST `/me/photos/{photoId}/appeals` | corpo vazio, If-Match da foto, Idempotency-Key | 201 PhotoAppealRequestedResponse |
| GET `/admin/photo-reviews/capabilities` | sessão administrativa/MFA | 200 PhotoReviewCapabilitiesResponse |
| GET `/admin/photo-reviews` | limit 1..50 (padrão20), cursor opcional | 200 PhotoReviewCasePageResponse; somente atribuições próprias vivas |
| GET `/admin/photo-reviews/{caseId}` | sessão/capacidade/atribuição vigente | 200 PhotoReviewCaseResponse + ETag do caso |
| POST `/admin/photo-reviews/{caseId}/assignments` | PhotoReviewAssignmentRequest; If-Match do caso; Idempotency-Key | 200 PhotoReviewMutationResponse |
| GET `/admin/photo-reviews/{caseId}/evidence/{variant}` | variant card/full/thumb | 200 image/webp em lease; sem cache/ranges/URLs |
| POST `/admin/photo-reviews/{caseId}/decisions` | PhotoReviewDecisionRequest; If-Match do caso; Idempotency-Key; TOTP recente | 200 PhotoReviewDecisionResponse |
| GET `/admin/photo-appeals` | limit 1..50 (padrão20), cursor opcional | 200 PhotoAppealPageResponse; somente atribuições próprias vivas |
| GET `/admin/photo-appeals/{appealId}` | sessão/capacidade/atribuição própria vigente | 200 PhotoAppealResponse + ETag do recurso |
| POST `/admin/photo-appeals/{appealId}/assignments` | PhotoReviewAssignmentRequest; If-Match do recurso; Idempotency-Key | 200 PhotoAppealMutationResponse |
| GET `/admin/photo-appeals/{appealId}/evidence/{variant}` | variant card/full/thumb | 200 image/webp em lease; sem cache/ranges/URLs |
| POST `/admin/photo-appeals/{appealId}/decisions` | PhotoReviewDecisionRequest; If-Match do recurso; Idempotency-Key; TOTP recente | 200 PhotoReviewDecisionResponse; ETag pertence ao recurso |

Sessão, CSRF e contexto usam os mecanismos existentes. Não criar autenticação local no frontend, alterar CF/MFA ou adicionar bypass. Sem capacidade explícita retorna403; sessão ausente401; TOTP recente ausente401/REAUTHENTICATION_REQUIRED. Outro titular e foto inexistente retornam o mesmo404. Caso/recurso sem atribuição não revela evidências. Cabeçalhos de comandos: Idempotency-Key válida1..64 caracteres ASCII alfanuméricos, `_` ou `-`; ausência/formato inválido400. If-Match ausente ou `*`428; fraco/lista/recurso errado/versão alterada412. Repetição autenticada da mesma chave/intenção devolve o recibo confirmado antes de comparar a versão atual; chave com outra intenção409. Conflito de estado/revisor indisponível409; armazenamento/evidência/lock indisponível503 com Retry-After; política não admitida503. Toda resposta privada usa Cache-Control:no-store.

## Estados e interface

Foto mantém os estados atuais, inclusive AwaitingReview e Rejected; pedido de recurso NÃO aprova foto. Caso inicial usa AwaitingReview/Approved/Rejected conforme a foto. Recurso: Open, ClosedApproved, ClosedRejected, ClosedUnavailable. Open possui alvo de resposta7dias desde pedido e limite duro30dias desde a rejeição inicial. Tempo excedido/arquivo ausente NÃO equivale a mérito humano nem aprovação. Decisão admite exclusivamente approve+meets_policy ou reject+does_not_meet_policy. Nenhum texto livre/sanção/seleção de foto do perfil.

Capabilities são dicas atuais de interface, nunca autorização reutilizável. Revalidar após401/403/409/412; nunca repetir uma decisão com chave nova automaticamente. Assinatura de atribuição não concede capability. Revisor de recurso deve ser diferente do autor da rejeição inicial e ter sua atribuição própria. A UI de evidência não lista thumbs nas filas; cria visualização somente após ação explícita, usa resposta binária privada e revoga objectURL ao sair. Não persistir imagens, estado de decisão ou headers no localStorage/log/telemetria. PT-BR/EN-US conforme padrões existentes. History não expõe ator/moderador, key/hash de storage ou original.

## Responsabilidades

Backend local implementa DTOs, consultas e comandos, migração de decisão de recurso, lease/audit/retenção e borda HTTP; frontend SS usa este contrato via ROOT. Campos ETag são strings; cabeçalho If-Match deve receber exatamente a string entregue, incluindo aspas. AssetId pode vir apenas após aprovação confirmada e não seleciona perfil. ResponseTargetAt é prazo de acompanhamento; MaximumRetentionUntil é limite de acesso. Os horários são UTC ISO8601 persistidos, sem cálculo de autorização no navegador.

OpenAPI e tipos gerados precisam refletir as rotas no mesmo head final. Não ocultar rotas, reduzir gates ou publicar heads parciais para obter geração. Nenhum teste sintético comprova dupla cobertura humana, provedor, VPS ou preservação operacional; PR256 e API/UI terão evidência própria por árvore.
