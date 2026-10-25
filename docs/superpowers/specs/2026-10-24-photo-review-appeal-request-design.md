# M4b1 — pedido de recurso da decisão humana

Recorte interno de173/69, derivado do ADR0035/P173-5 e docs/api/media-review.md. O titular pede nova revisão de uma rejeição humana até rejectedAt+7dias, usando versão da foto e chave idempotente. Não permite recurso de expiração, indisponibilidade, rejeição técnica ou aprovação. Não muda Rejected para AwaitingReview/Approved, não cria decisão humana ou sanção e não concede leitura ao decisor anterior. A decisão do recurso e seus acessos/atribuições vêm noM4b2; exigirão pessoa distinta e capacidadeAppeal real, sem presumir cobertura operacional.

## Serviço e autorização

PhotoReviewAppeals.RequestAsync(PhotoReviewActor owner,Guid photoId,long expectedPhotoVersion,IdempotencyKey key,CancellationToken). Identificadores vêm da futura borda autenticada; o par não é credencial pública. Serviço interno sem DI/HTTP/flag. Validar sessão Normal viva, stamp atual, usuário confirmado sem senha temporária, não em exclusão e sem papel privilegiado; não exigir papel/capacidade de moderador do titular. Restrição de publicação não transforma recurso em publicação: o pedido não depende de elegibilidade para publicar, mas continua exigindo sessão válida. Alheio/inexistente uniformementeNotFound. Revalidar após waits e imediatamente no commit.

READCOMMITTED própria, recusar ambientTransaction. ownerSafetyguard→IdentitySHARE→papéisSHARE NOWAIT→sessãoSHARE→uploadFORUPDATE→casoFORUPDATE→candidatoFORUPDATE. Descoberta não autoriza. Revalidar dono, estadoRejected/PHOTO_REVIEW_REJECTED, geração/caso/política, versões e único fato humano reject/does_not_meet_policy correspondente. Candidato committed/writer-settled sem limpeza/deleted/lease e retenção original igual ao snapshot7dias. Sem aquisição de outro usuário nesta etapa. Idempotência/recibo depois de autenticação viva e antes de versão/prazo/storage; intenção é foto+pedido sem texto livre, alteração de If-Match não altera intenção.

## Bytes e commit

Ler/verificar as três variantes sanitizadas foraSQL com orçamento15s e manifestos canônicos/tamanho/hash. Reabrir transação e revalidar toda autorização/contexto/tempo depois do I/O. Ausência/corrupção retornaUnavailable sem recurso nem promoção; reconciliação persistente de PHOTO_REVIEW_UNAVAILABLE é entregaM6 própria. Não reconstruir a partir do original. Não logar chaves/bytes.

Na mesma transação: criar root de recurso ligado à decisão anterior, estender candidato até rejectedAt+30dias (teto duro, sem hold ilimitado), incrementar foto/caso e gravar recibo/auditoriaUser. response_target_at=requestedAt+7dias é alvo operacional, não deferimento nem encerramento automático. Um pedido recebido antes do limite ganha retenção mesmo se o processo de coleta começar depois; o coletor de rejeições terá de adquirir o mesmo lock do candidato e conferir recurso/retention. Sem coletor de rejeições ainda, não alegar corrida recurso×GC demonstrada.

Schema aditivo apósRED: root safety.photo_review_appeals e recibos safety.photo_review_appeal_commands, FKrestrict e UNIQUE por decisão original, limites UTC finitos e geração vinculada, sem backfill. Root Open/version1; encerramento posterior exige mudança revisada. Recibos append-only com hashes32bytes e resultado mínimo. REVOKE DELETE/TRUNCATE root e UPDATE/DELETE/TRUNCATE recibos para duora_app; atualizar a reconciliação canônica na composição226 antes de instalação. Nenhum grant a pessoa real. Preservar decisões/manifests imutáveis anteriores e auditoria mínima1ano, sem texto/imagem.

## Aceite e sequência

PostgreSQL/filesystem sintéticos em CI hospedada: abertura/versões/retention/audit atômicos; mesma chave/intenção replay após prazo e versão avançada; mesma chave outra fotoConflict; versão vencida; titular alheio/inexistente; sessão revogada/expirada/stamp/role trocado; limite7dias antes/exato; origens não humanas recusadas; concorrência dois pedidos1efeito; evidência ausente/corrompida; falha de auditoria e COMMITincerto; guarda real com revogação/exclusão/clock avançando durante espera. Bytes foraSQL e nenhuma cópia/publicação. PrimeiraexecuçãoREDcomscaffold, depoisproduto/revisão/CI.

Não há SDK/build/test/Docker no Mac, SSH, banco real, publicação, novaCI enquanto fatia anterior executa, integraçãoMain ou instalação125. DefaultsHumanReview continuam desligados. Esta etapa não satisfaz sozinha recurso completo, retenção/coleta de rejeitados, exclusão/restore/backup/UI ou ativação173/69.
