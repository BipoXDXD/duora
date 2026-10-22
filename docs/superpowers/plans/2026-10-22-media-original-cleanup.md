# Limpeza durável do original — #173/M2a e dependência de #174

> Execução na branch isolada `feat/173-human-review`, com testes exclusivamente na CI. Uma revisão independente da composição final antecede o merge autorizado pelo responsável.

**Objetivo:** falha de storage ou queda após o commit não deixa o original permanentemente na quarentena. Reprocessar a limpeza não decodifica novamente, não decide o upload e não toca derivados aprovados ou assets do catálogo.

**Arquitetura:** fila SQL específica de Media, criada vazia por migration aditiva. O mesmo commit da transição terminal registra a intenção de apagar o original. O adaptador tenta apagar imediatamente; um consumidor com lease/fencing retoma intenções duráveis após falha ou reinício. I/O do storage acontece fora de qualquer transação/lock de negócio.

**Base normativa:** ADR-0035, `docs/api/media-review.md` (original eliminado após derivação/commit com limpeza durável), INV-06/08/11, T-06/08/16/26. Esta etapa não cria escritores HumanReview, políticas, rotas, permissões ou montagens. M2b–M7 continuam entregas próprias.

## Restrições globais

- Nenhum Docker, build, instalação ou teste pesado no Mac. Nenhuma intervenção manual em produção.
- Dados de teste exclusivamente sintéticos e PostgreSQL real no runner.
- Não varrer diretórios nem semear exclusões para registros existentes. Só processar intenções emitidas pelas transições desta versão.
- Intenção contém a chave imutável de quarentena lida do upload; a fila não aceita paths, derivados ou parâmetros do cliente.
- Não relaxar check, timeout, scanner, autenticação, cota ou gate. API, contratos, schema existente e binário compatível permanecem preservados.
- Sucesso da limpeza não aprova foto nem altera decisões. Falha mantém intenção durável, com backoff limitado, sem desistência silenciosa.

## Arquivos e interfaces

- `backend/src/Duora.Infrastructure/Persistence/Migrations/20261022143000_OriginalCleanup.cs`: tabela SQL aditiva, sem seed e sem Down destrutivo.
- `backend/src/Duora.Infrastructure/Media/OriginalCleanupQueue.cs`: `EnqueueAsync(DuoraDbContext, Guid, DateTimeOffset, CancellationToken)` exige transação aberta e lê a chave/estado no banco; `CompleteImmediateAsync` remove só a intenção exata após DELETE idempotente confirmado.
- `backend/src/Duora.Infrastructure/Media/OriginalCleanupDispatcher.cs`: `DispatchOnceAsync(CancellationToken)` toma uma intenção por lease de 60 segundos, impõe orçamento total de 15 segundos ao DELETE, executa fora da transação e conclui por token/lease; em falha, reagenda até 15 minutos, sem expirar a obrigação. O orçamento domina os timeouts/retries maiores do S3 e não há lote que envelheça antes do I/O.
- `backend/src/Duora.Infrastructure/Media/MediaProcessingDispatcher.cs`: enqueue no commit de Approved/Rejected, tentativa imediata após commit, sem alterar o contrato do construtor nem a porta `IPhotoSanitizer` usada por S3.
- `backend/src/Duora.Infrastructure/Media/MediaProcessingServiceCollectionExtensions.cs`: registra consumidor e sua configuração de disponibilidade a partir do provider de storage.
- `backend/src/Duora.Worker/Media/OriginalCleanupService.cs` e uma inscrição em `WorkerHost.cs`: laço independente da admissão de uploads; provider Unset não consome. Não instala serviço nem altera configuração da VPS.
- `backend/tests/Duora.IntegrationTests/Media/OriginalCleanupTests.cs`: regressão real de storage indisponível/reinício, com sucesso/rejeição, uploads desligados e preservação de derivados.

## Tarefa 1 — reproduzir a perda da obrigação de exclusão

- [ ] Acrescentar o teste `CommittedOriginalDeletionIsRetriedAfterRestartWithUploadsDisabled(bool approved)` à classe parcial MediaPipelineTests.
- [ ] Receber upload pelo HTTP existente, recusar o DELETE do original e executar uma entrega técnica; confirmar Approved/Rejected e original ainda presente.
- [ ] Conferir a intenção no PostgreSQL e descartar o primeiro host; reiniciar o Worker sobre o mesmo banco/storage, com upload desligado, e permitir DELETE. Exigir original ausente em até 10 segundos, intenção consumida e mesmo estado/asset/derivados. Um sanitizador contador no segundo host deve continuar com zero chamadas.
- [ ] Push/PR de regressão; observar falha real na CI antes da implementação. Não alterar os testes/gates para fazê-la passar.

## Tarefa 2 — persistir e consumir a obrigação

Tabela SQL:

```sql
create table media.original_cleanup (
  upload_id uuid primary key references media.uploads(id) on delete restrict,
  object_key varchar(128) not null unique,
  created_at timestamptz not null,
  available_at timestamptz not null,
  attempts integer not null default 0 check (attempts >= 0),
  fencing_token uuid null,
  lease_until timestamptz null,
  check ((lease_until is null) = (fencing_token is null)),
  check (object_key ~ '^quarantine/[0-9a-f]{32}$')
);
create index ix_original_cleanup_available on media.original_cleanup(available_at, upload_id);
```

- [ ] Inserir intenção com `INSERT … SELECT` do upload em Approved/Rejected/Deleted/AwaitingReview e `ON CONFLICT DO NOTHING`; exigir transação do efeito.
- [ ] Transformar Reject em transação curta de estado + intenção; acrescentar intenção à transação já existente de Approved.
- [ ] Depois do commit, tentar DELETE e remover a intenção exata somente após sucesso. Falha não desfaz a decisão confirmada.
- [ ] Claim individual por CTE com `FOR UPDATE SKIP LOCKED`, trocar tokenUUID aleatório/incrementar attempts e encerrar a transação antes do storage. A posse não se repete ao remover/recriar a linha (ABA). DELETE recebe token ligado ao shutdown e orçamento total de15s, inferior ao lease60s com margem SQL. Conclusão/reagendamento exigem token atual e lease válido; lease vencido pode ser retomado.
- [ ] Capturar erro por tipo fechado no log, nunca mensagem, chave, conteúdo ou caminho do objeto. Cancelamento deixa lease expirar.
- [ ] Consumidor não depende de Media:Review estar habilitada; provider ausente suspende o laço.

## Tarefa 3 — concorrência, atomicidade e compatibilidade

- [ ] PostgreSQL real: transação que volta atrás não deixa intenção; DELETE repetido de objeto já ausente é sucesso; dois consumidores não concluem a mesma posse.
- [ ] Lease vencido/reclamado não aceita ACK/retry do token anterior; falha após remover o objeto e antes do ACK é recuperável sem tocar derivados.
- [ ] DELETE lento/cancelado mantém intenção e libera posse somente por retry/expiração; orçamento é individual e domina RequestTimeout/retries do S3.
- [ ] Rejeição e aprovação com falha de DELETE preservam o estado confirmado e a outbox; sanitizador não é chamado pelo cleanup.
- [ ] Candidatos/derivados/catálogo e registros anteriores sem intenção não são varridos ou removidos.
- [ ] Repetir o teste do binário publicado existente sobre a expansão aditiva e conferir a árvore/hash de todos os assets da base.

## Integração

- [ ] Revisão independente sem bloqueadores, nove checks gerais verdes no head final e matriz de isolamento aplicável preservada.
- [ ] Merge somente após a janela de coordenação do #197 terminar e revisão exigida pela política do repositório; promoção exclusivamente automática.
- [ ] Registrar evidência nas issues #173/#174 sem encerrá-las enquanto etapas editoriais, homologação e cobertura independente continuarem pendentes.
