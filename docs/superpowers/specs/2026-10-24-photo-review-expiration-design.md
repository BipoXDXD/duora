# M6b — vencimento da espera editorial e limpeza confirmada

Escopo de engenharia/produto delegado em24/10/2026 para concluir173/69. Implementação própria, empilhada no235/c531801, com núcleo226 e seus oito ancestrais preservados. Não alterar125v2, M2/CF/APT, main ou ambiente real. O contrato aceito é ADR0035/P173-5 e docs/api/media-review.md: espera72h, limpeza até24h depois, sem decisão automática.

## Resultado e fronteiras

Ao atingir review_deadline, um caso ainda AwaitingReview passa atomicamente para Rejected/PHOTO_REVIEW_EXPIRED. É um fato System: não inserir linha em safety.photo_review_decisions, criar recurso fictício, sancionar conta ou publicar asset. A foto ganha versão; o caso ganha versão; auditoria e obrigação de limpeza pertencem ao mesmo commit. Pending/Processing/Approved/Rejected/Deleted e casos sem candidato correspondente não são matriculados.

PhotoReviewExpirations(IDbContextFactory<DuoraDbContext>,TimeProvider).ExpireOnceAsync(CancellationToken) retorna0/1. Serviço inicialmente interno e sem DI/HTTP/flag; a composição de consumidores vem noM7 com seus gates. O vencimento se baseia no relógio UTC depois de todas as esperas. Antes do prazo retorna0; no instante exato vence; retry/concorrência/restart não repetem efeitos.

## Transação e concorrência

Descobrir caso/upload/titular/geração sem locks. Em READCOMMITTED própria, sem Transaction.Current, adquirir safety.user_guards do titular antes de IdentitySHARE e de upload→caso→candidato; usar lock_timeout2s e a ordem dos coordenadores existentes. Revalidar identidade/geração/versões, committed/writer-settled, nenhuma limpeza/exclusão e prazo. Não exigir elegibilidade para manter foto expirada presa por bloqueio; exclusão/decisão concorrente vencem pelo estado/versão sob as mesmas guardas. Nenhum I/O de storage ou transação longa.

Atualizar upload/caso por CAS, anexar AuditAction.PhotoReviewExpired/System com targetcase/version, obter por SELECT o único idbigint dessa ação/caso/versão na mesma transação e ligá-lo ao candidato. AuditTrail atual não muda: não inferir id porMAX, UUID ou sequência global. Consulta ambígua/inexistente é falha fechada e rollback integral. Falha/resposta incerta de COMMIT não confirma resultado falso; retry consulta estado persistido.

## Persistência e coleta

Migração aditiva: media.review_candidates.cleanup_audit_id bigint nullable com FK restrict para ops.audit_events; valores existentes ficamNULL. Índice parcial apenas sobre marcadores confirmados do novo fluxo. Nenhuma tabela/grant/backfill, mudança de migração aplicada ou ACLlegada.

ExpiredCandidateCleanupDispatcher separado do abandonado e do aprovado. Só consome candidato committed/writer-settled com marcador devido e cleanup_audit_id ligado à ação System/expired, caso/geração/upload/versão e código expirado correspondentes. Não usa arquivo antigo, enumeração de diretório ou estadoRejected isolado. Exclusões futuras que invalidem esse contexto exigirão sua própria obrigação revisada; nunca ampliar a consulta cegamente.

Claim SKIPLOCKED/tokenUUID/lease60s, DELETE15s das três chaves canônicas review-candidates/{generation}-{kind} foraSQL, backoff persistido e ACK só com token/lease/contexto vigentes. GC não altera estado/decisão/auditoria, originais, derivados finais ou outra geração. Falha/cancelamento/lease vencido deixam obrigação recuperável. Sem limite de tentativas que descarte pendência.

## Aceite e testes necessários

PostgreSQL/filesystem sintéticos reais, somente CI hospedada: fronteira72h antes/no/depois; versões/código/evento/marcador atômicos; zero decisão humana/asset; repetição/restart/duas expirações; aprovação/rejeição/exclusão concorrentes e expiração esperando guarda; auditoria indisponível e COMMIT incerto; contexto divergente sem efeito. GC: três chaves próprias, outros/finais/histórico preservados, marcador/prova divergentes recusados, lease expirado antes/depois de troca de token, parcial/backoff/shutdown, SQLlock liberado antes do DELETE.

RED comportamental antes do produto, revisão independente e gates da composição final. CIs/resultado235 não qualificam esta fatia nova. Sem SDK/Docker/testes no Mac, instalação, dados reais ou alterações operacionais. Este recorte não implementa recurso, hold excepcional, unavailable/restore, DELETE do titular, backupApproved-only ou habilitaçãoHumanReview; seguem entregas separadas até o critério integral173/69.
