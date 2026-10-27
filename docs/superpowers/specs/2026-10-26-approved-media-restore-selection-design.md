# Seleção privada de restore Approved-only

## Propósito e limite

Resolver a junção de código ausente entre catálogo SQL Approved, manifesto privado do snapshot e tombstones externos (INV-11/T-11, critérios69-04/173-04/07). Este componente produz uma seleção privada ou recusa integral. Não copia bytes, executa restic, altera banco/arquivos ativos, registra hosted service, oferece endpoint ou admite restore/ativação. O PR263 e seus nove blobs permanecem intactos. A implementação foi autorizada pelo proprietário via coordenação; revisão por agentes substitui aprovação humana adicional do PR.

O catálogo já aplica MVCC somente leitura e valida autorização/commit/variantes. O validador DeletionLedgerValidation já autentica uma barreira de epoch/fence independente e recusa histórico incompleto. Nenhum dos dois deve ser reimplementado ou enfraquecido.

## Alternativas

1. Acrescentar somente PhotoId ao catálogo: fecha a projeção, mas não aplica tombstones à seleção.
2. Acrescentar PhotoId/Generation e um seletor privado limitado, com autenticação obrigatória e validação do ledger existente: escolhido, resolve a junção sem efeitos operacionais.
3. Exportador/restic/DELETE/restore físico completo: requer contratos adicionais de snapshot/imutabilidade, custódia, abertura segura e operação. Continua em recortes posteriores; não é escondido neste componente.

## Contratos privados

- ApprovedMediaCatalogAsset passa a conter PhotoId lógico (=media.uploads.id), AssetId, Version do asset e PromotionGeneration anulável, além das três variantes existentes. A consulta continua read-only, sem nova tabela/migração/privilegio.
- ApprovedMediaRestoreManifest V1 é metadado privado: EnvironmentId, SnapshotId, SourceDigest OCI, CapturedAt UTC, DatabaseSnapshot original, assets PhotoId/AssetId/Version/PromotionGeneration com três variantes (kind/key/dimensões/tamanho/hash), TotalBytes, KeyId e assinatura. Não é DTO/JSON aceito por rota. Um adaptador confiável deve autenticar TODOS os campos da cópia congelada; sem autenticador ou falha/exception, recusa. Nenhum adaptador real ou assinatura é inventado nesta entrega.
- ApprovedMediaRestoreExpectation vem de custódia independente, nunca do JSON/banco restaurado: EnvironmentId, SnapshotId e SourceDigest esperados; limites máximos de assets/bytes. A expectativa DeletionLedgerExpectation externa e sua barreira/histórico completos são entradas separadas. O ambiente tem de coincidir; RestoreEpoch é novo e externo, validado pelo componente existente.
- O seletor recebe o catálogo do banco restaurado, manifesto e expectativas/barreira/entradas/autenticadores. Invoca DeletionLedgerValidation diretamente; não aceita um Verified fabricável como autoridade.

## Algoritmo e invariantes

1. Validar limites: até4096assets, até24MiB/asset e total limitado por4096*24MiB; três variantes canônicas por asset, <=16MiB/variante, dimensões positivas <=20M pixels, hashes hex SHA256 minúsculos, versões positivas e IDs não vazios. O seletor limita o orçamento independente do ledger a65536entradas: exceder o teto recusa, sem truncar histórico. Isso é limite deste componente, não medição/admissão de capacidade do host. Metadados snapshot são limitados a65536caracteres; assinatura a4096bytes e keyid a128caracteres. Contagens/bytes/overflow/duplicatas são verificadas antes de devolver resultado. Entradas mutáveis são copiadas em coleções privadas; autenticação incide sobre a cópia usada na comparação.
2. Manifesto completo deve ter formato1, identidade independente exata e assinatura/keyid presentes. Validar cada entrada, inclusive a posteriormente omitida: somente derivatives, três kinds distintos, chaves únicas globalmente e geração promovida coerente. Catálogo já é privado/autoritativo, mas seus limites, duplicatas e variantes também são checados; não confiar em dados forjados por chamador.
3. Validar o ledger completo pelo validador existente, incluindo autenticação externa, checkpoint/tail/fence e operações terminais. Cancelamento é observado durante cópia/comparação e propagado; nenhuma recusa inclui plano parcial.
4. Tombstone PhotoId exclui qualquer versão/geração daquele PhotoId. Entrada sem asset atualmente Approved no catálogo restaurado é omitida, sem reconstruir original/candidato nem reaprovar. Para uma PhotoId presente nos dois, AssetId/Version/Generation e todas as variantes/key/dimensões/tamanho/hashes têm de coincidir; divergência recusa o lote.
5. Promovidos têm hashes persistidos de promoção/decisão, já conferidos pelo catálogo. Só os três objetos exatos de uma seleção elegível entram no resultado. Legado com ExpectedSha256 NULL permanece recusado caso elegível: hash medido de snapshot não é hash histórico do banco, e imutabilidade/snapshot consistente legado ainda não foi demonstrado. Legado tombstoned/fora de Approved não é selecionado.
6. Resultado é cópia somente leitura com identidade snapshot/environment/restoreEpoch e lista privada das variantes selecionadas. ToString de manifestos/assets/resultados não revela IDs/chaves/hashes. Seleção não confere autoridade para aplicar arquivos, admitir restore ou iniciar provider.

## Evidência e execução

Novos testes da integração existente usam PostgreSQL real para projetar PhotoId/Generation e ligar uma aprovação inicial/recurso ao seletor. Testes puros no assembly IntegrationTests cobrem seleção positiva, tombstone posterior do mesmo PhotoId, outra foto preservada, nãoApproved ausente, legacy recusado, assinatura/identidade/ledger ausentes ou inválidos, prepared não resolvido, forks/tail via validador existente, duplicatas/missing variants/hash/key/version/gen/byte mismatch, overflow/cancelamento/exception e dados de entrada imutáveis. Nenhum teste duplica a suíte completa do ledger.

Teste primeiro com contrato mínimo recusando por padrão, RED de comportamento ausente no CI normal; implementação somente após recibo RED. Não executar SDK/build/test/Docker no Mac e não criar lane/manual dispatch. Revisão independente estática antes da preparação RED e após implementação. Uma CI normal final qualifica a composição; mudanças posteriores de main são reconciliadas pelo integrador M2.

## Pendências que esta entrega não encerra

Adaptador real de autenticação/custódia/ledger e transportes/receitas, API-M03/M04/perfil e exclusão, exportador/backup Approved-only físico, snapshot/imutabilidade legado, restore físico/importador, B2/COMPLIANCE/retensão, timers/mounts/UID/media-verify/capacidade e cobertura humana/admissão produtiva continuam com seus donos. Issues69/173 permanecem abertas até critérios reais; nenhum fechamento administrativo equivale a resolução.
