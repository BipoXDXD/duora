# Candidato privado e caso durável — #173/M2b

> Execução inline na branch `feat/173-review-candidates`, inicialmente filha do #198/M2a. Testes executáveis somente na CI; revisão independente e todos os checks antes de integrar. Rebase/retarget para main depois do merge de M2a.

**Entrega incremental:** #203/M2b-1 contém somente chaves/área e testes dos adaptadores (Tarefa 1). Tarefas 2–4 são o plano das próximas fatias, ainda sem implementação. Os testes RED de 72f5b76 foram observados na CI39798090643: três casos unitários e filesystem/MinIO falharam no parser inexistente; duas falhas herdadas de inventário de privilégios foram corrigidas em #198/7d313c0 antes desta implementação.

**Rascunho #205/M2b-2:** implementação das tarefas 2–4 em validação, ainda fora do pipeline/política. RED observado em 0c13177/CI39802046717: 5 falhas comportamentais esperadas, 3229 testes aprovados e 0 ignorados. Testes adversariais adicionais foram escritos antes da implementação e revisados, mas ainda aguardam sua execução na CI; não lhes atribuir o RED anterior. A revisão do código também exigiu negar replay vencido/sem bytes, com prazo e hashes verificados sem locks; o writer público de indisponibilidade permanece em M6. O novo GC tem testes próprios de fencing, concorrência, DELETE parcial, shutdown e obrigação incerta sem expiração.

**Objetivo:** persistir apenas os três derivados sanitizados numa área privada exclusiva por tentativa e confirmar AwaitingReview + caso Safety + auditoria + obrigação de apagar o original numa transação curta, sem criar asset aprovado.

**Spec:** ADR-0035 e `docs/api/media-review.md`, M2. INV-03/05/06/08/10/11; T-03/05/06/08/10/16/26. O contrato aceito e a autorização atual de implementar as pendências definem este recorte. A entrega não habilita HumanReview, endpoints, permissões, montagens ou uso de dados reais; M3–M7 continuam necessários.

**Arquitetura:** Media possui metadados/arquivos da tentativa; Safety cria o caso por operação própria, na mesma unidade transacional. A geração aleatória de uma tentativa nunca é reutilizada por outra. Intenção de limpeza existe antes do primeiro PUT; processo que cai não deixa arquivos sem referência durável de recuperação. Storage permanece fora de locks.

## Restrições e compatibilidade

- Não alterar o array legado obrigatório `FileSystemObjectStore.AreaDirectories`: ele continua quarantine/derivatives e o boot atual continua válido com uploads desligados.
- `review-candidates` será exigida por configuração/montagem somente em M7 quando o fluxo estiver coerente. Em Testing o adaptador pode criar sua área temporária; em Production ele continua recusando diretório ausente.
- Não misturar candidato com derivatives nem com o backup de mídia corrente. Nenhuma URL, chave, original ou geração privada é projetada para o cliente.
- Não criar enum HumanReview disponível nem ligar o Stage ao dispatcher nesta fatia. O serviço é exercitado diretamente por testes sintéticos antes de M7.
- Não acrescentar FK reversa na raiz M1 `safety.photo_review_cases` que invalide seus leitores/fixtures existentes. Metadados novos podem referenciar o caso; a operação Safety exige correspondência de upload/geração.
- Nenhuma dependência nova. Sem Docker/build/testes/instalações no Mac, sem cancelamento e sem intervenção manual em produção.

## Arquivos e interfaces

- `Duora.Core/Media/ObjectKey.cs`: área `ReviewCandidates`, `ForReviewCandidate(Guid generation, PhotoVariant variant)`, parser fechado e ToString redigido. Identificadores públicos nunca geram essas chaves; Stage cria Guid aleatório próprio.
- `Duora.Infrastructure/Media/ReviewCandidateRecords.cs`: projeções SQL internas com geração, upload/versão, dimensões/tipo detectado, três variantes/hash, prazo de preparação, commit, retenção e intenção/posse de limpeza.
- Migration SQL aditiva `20261022150000_PrivateReviewCandidates`: tabelas novas vazias, sem seed, atualização ou exclusão de dados antigos. Mapeamento operacional segue o padrão SQL de M1/M2a; não gerar modelo EF divergente.
- `Duora.Infrastructure/Media/PhotoReviewCandidates.cs`: `StageAsync(Guid uploadId, long expectedVersion, DateTimeOffset leaseExpiresAt, SanitizeOutcome.Sanitized sanitized, CancellationToken ct)` retorna `Staged(caseId)` ou `LeaseLost`. A operação recebe somente prazo do claim confiável, nunca prazo de cliente.
- `Duora.Infrastructure/Safety/PhotoReviewCases.cs`: criação do caso `human-review-v1` exige transação aberta e candidato/geração correspondentes. Registra o fato de solicitação na auditoria, sem decisão ou dados pessoais adicionais.
- `Duora.Infrastructure/Media/ReviewCandidateCleanupDispatcher.cs` e inscrição Worker: recuperam exclusivamente intenções de tentativas falhas/expiradas, com token UUID e I/O sem locks; não expiram revisão humana nem removem candidatos confirmados. O restante da retenção pertence a M6.

## Estrutura SQL

```sql
create table media.review_candidates (
  generation uuid primary key,
  upload_id uuid not null references media.uploads(id) on delete restrict,
  upload_version bigint not null check (upload_version >= 1),
  detected_type varchar(32) not null check (detected_type in ('image/jpeg','image/png','image/webp')),
  width integer not null check (width > 0),
  height integer not null check (height > 0),
  created_at timestamptz not null,
  prepare_deadline timestamptz not null,
  committed_at timestamptz null,
  case_id uuid null references safety.photo_review_cases(id) on delete restrict,
  retention_until timestamptz null,
  cleanup_requested_at timestamptz null,
  writer_settled_at timestamptz null,
  last_cleanup_at timestamptz null,
  deleted_at timestamptz null,
  cleanup_token uuid null,
  cleanup_lease_until timestamptz null,
  cleanup_available_at timestamptz not null,
  cleanup_attempts integer not null default 0 check (cleanup_attempts >= 0),
  unique(upload_id, upload_version),
  check (width::bigint * height <= 20000000),
  check (prepare_deadline > created_at),
  check ((committed_at is null) = (case_id is null)),
  check ((committed_at is null) = (retention_until is null)),
  check ((cleanup_token is null) = (cleanup_lease_until is null)));
create table media.review_candidate_variants (
  generation uuid not null references media.review_candidates(generation) on delete restrict,
  kind varchar(8) not null check (kind in ('thumb','card','full')),
  object_key varchar(128) not null unique,
  width integer not null check (width > 0),
  height integer not null check (height > 0),
  size_bytes bigint not null check (size_bytes between 1 and 16777216),
  sha256 bytea not null check (octet_length(sha256)=32),
  primary key(generation,kind));
```

A implementação acrescenta constraints de datas finitas, chave correspondente à geração/variante e consistência de commit/limpeza. Nenhuma dessas tabelas permite leitura pública. O teto de saída é o candidato já aceito para ensaio no ADR-0036, sem homologação de produção implícita.

## Tarefa 1 — área e limites de armazenamento

- [x] RED na CI: ler chaves review-candidates válidas e armazenar/excluir candidato sem afetar quarantine/derivatives no filesystem e no MinIO existentes.
- [ ] Gerar chaves de tentativa com geração não vazia, tipo/variante fechados, parser ancorado no fim real e ToString sem token.
- [ ] Testar geração diferente, path traversal, maiúsculas, variante desconhecida, quebra de linha, chave candidata recusada como origem de ForDerivative, ausência de diretório em modo sem criação e ACL privada no S3.

## Tarefa 2 — preparação fora dos locks

- [ ] Primeiro procurar confirmação da mesma tentativa: revalidar conta, upload AwaitingReview/version+1, geração/caso coerentes e ainda disponíveis. Devolver o caso confirmado sem novos PUTs ou efeitos. Só na ausência desse replay exigir Processing na versão esperada e fingerprint válido; recusar prazo já vencido/fora do lease configurado e forma inválida de variantes.
- [ ] Criar geração própria, persistir metadados e exatamente três chaves/hashes antes de qualquer PUT. Unicidade por upload/versão impede dois preparadores da mesma tentativa; o perdedor não grava por cima do vencedor.
- [ ] Escrever thumb/card/full em chaves da geração fora de transações de negócio, com token/prazo total da tentativa. A geração nunca é retomada para escrita por outro Stage. Falha conserva metadados para GC e nunca cria asset ou caso público.
- [ ] `writer_settled_at` somente após todos os PUTs terem respondido sucesso, ou saída comprovada antes do primeiro PUT. Exceção de transporte/cancelamento/queda não prova que o servidor encerrou a escrita e mantém o escritor incerto. Mesmo após marcar settled, revalidar cleanup_requested_at/deadline antes de commit; nunca emitir outro PUT nessa geração.

## Tarefa 3 — commit coordenado

- [ ] Começar transação curta com teto de lock. Guardar usuário e sua linha Identity na ordem existente de ParticipationSql.Guard; revalidar e-mail/status permitido, upload/versão e prazo após os locks.
- [ ] Travar geração; exigir preparo não expirado, nenhum pedido de limpeza e ausência de commit anterior.
- [ ] Safety cria caso com política human-review-v1, AwaitingReviewAt atual, deadline +72h e versão1. Media confirma os metadados/caseId e retention_until inicial +96h, e muda upload Processing→AwaitingReview por CAS.
- [ ] Auditoria System/solicitação e OriginalCleanupQueue.EnqueueAsync participam do mesmo commit. Nenhum asset aprovado é criado e nenhum lease técnico fica aguardando humano.
- [ ] Depois de commit confirmado, tentar apagar original usando a intenção M2a. Falha ambígua de COMMIT não pode marcar para limpeza uma geração que já foi confirmada; o CAS de abandono aceita somente committed_at nulo.
- [ ] Repetição do Stage confirmado devolve o caso existente, sem novo efeito ou reencoding. Versão/conta/geração vencidas falham fechadas e não fazem publicação.

## Tarefa 4 — recuperação e testes de composição

- [ ] Consumidor toma uma geração não confirmada, expirada/abandonada, por lease/tokenUUID. O claim marca cleanup_requested_at antes dos DELETEs; Stage não pode confirmá-la depois.
- [ ] Remover somente as três chaves daquela geração fora da transação. Falha parcial mantém obrigação; ACK/retry exigem posse atual. Nunca apagar geração confirmada nem chaves de outra tentativa.
- [ ] O claim captura sob lock se `writer_settled_at` já existia **antes de começar essa passagem de DELETEs**. Somente esse claim settled pode fazer ACK terminal após excluir todas as chaves. Uma passagem iniciada com escritor incerto deve reagendar, mesmo que o escritor marque settled antes do ACK: pode ter recriado uma chave que esse GC já apagou.
- [ ] Se o claim começou incerto, não encerrar a obrigação após os DELETEs: gravar `last_cleanup_at`, liberar posse e reagendar limpeza periódica. Conservar tombstone/intenção sem expiração por lease/contador; um PUT tardio ainda pode recriar a chave. Um escritor vivo que obtém sucesso tardio pode marcar settled, mas não confirmar candidato após cleanup_requested_at; a próxima coleta encerra os bytes depois dele. Tombstones incertos exigem prova operacional de término para eventual pruning; esta fatia não inventa prazo que torne uma escrita incerta segura. O custo de metadados e DELETEs repetidos deve aparecer no relatório, sem ampliar retenção dos bytes.
- [ ] Teste determinístico obrigatório: bloquear PUT que ignora cancelamento, expirar tentativa, GC apagar sem ACK terminal, liberar PUT tardio e provar remoção eventual; manter outra geração confirmada byte a byte.
- [ ] Testar também GC apaga primeira chave, PUT tardio a recria e escritor marca settled antes do ACK: essa primeira passagem conserva a obrigação; a próxima passagem remove tudo.
- [ ] PostgreSQL/storage: falhas entre cada PUT, depois dos arquivos/antes do commit, na auditoria, perda de versão/lease, restrição concorrente, repetição, restart e commit ambíguo. Conferir hashes dos derivados/catálogo sentinela e contagens diretas de casos/assets/outbox/intenção original.
- [ ] Restaurar banco sem candidato não o torna aprovado; testar metadados conservados e leitura negada. Reconciliar decisão pública de indisponibilidade em M6, sem inventar writer parcial nesta fatia.
- [ ] Repetir gate de leitores publicados sobre as migrations aditivas, nove checks gerais e isolamento aplicável no head final. Revisão independente sem bloqueadores antes de merge; manter #173/#174 abertas pelos critérios restantes.
