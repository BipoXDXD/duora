# Escritores interrompidos de candidatos e promoções privadas

Investigação de 31/10/2026 sobre a `main` em `b4bdee1`, após os PRs
[#301](https://github.com/BipoXDXD/duora/pull/301) e
[#303](https://github.com/BipoXDXD/duora/pull/303).

## Resultado e escopo

**Não há prova suficiente no protocolo atual para concluir uma geração cujo
`writer_settled_at` permanece nulo.** Preservar a repetição da exclusão e a retenção
do tombstone. Esta alteração acrescenta testes e documentação; não muda os
escritores, coletores, SQL de retenção, privilégios nem configuração operacional.

INV-06/08/11/12; T-08/11/16/26. Relação com API-M01/M03/M04: preparar bytes privados,
excluir uma foto sem reaparecimento e continuar negando leitura de foto excluída.
Nenhum contrato HTTP, autorização ou gate de upload muda. A ativação da #69 e os
gates de isolamento, backup/restore e release continuam separados.

## Protocolo examinado

| Fronteira | Garantia existente | O que ela não prova |
|---|---|---|
| `PhotoReviewCandidates.PrepareAsync` | Persiste geração e manifesto antes dos PUTs; unicidade por upload/versão | Não existe fechamento da geração imposto pelo storage; candidato nasce com escritor incerto mesmo antes do primeiro PUT |
| `PhotoReviewPromotions.PrepareMetadataAsync` / `BeginWritingAsync` | A promoção nasce assentada, sem I/O; UPDATE condicional retira o assentamento antes do primeiro PUT | Uma vez confirmado o início, uma pausa após o último teste de prazo ainda pode preceder o PUT |
| `StageAsync` / `PhotoReviewPromotions.PrepareAsync` | PUTs sequenciais, prazo e cancelamento cooperativo; assentamento após sucesso e ausência de novos PUTs no fluxo | Exceção/cancelamento ou morte entre o I/O e seu ACK durável deixam resultado incerto; `finally` não é garantia contra morte do processo |
| Commit de candidato / confirmação e aprovação de promoção | Revalida estado, versão, conta, prazos e ausência de limpeza sob locks curtos | Bloqueia publicação no banco; não revoga uma operação de storage já admitida |
| Claim / retry / conclusão dos coletores | Token de posse, lease e `FOR UPDATE SKIP LOCKED`; ACK/retry antigo não altera posse nova | O token só é comparado no PostgreSQL. `IObjectStore.PutAsync` não recebe geração fechada, epoch ou token de fencing |
| `CompleteSweepAsync` | Fotografa o assentamento no claim; `deleted_at` só se assentado antes dos DELETEs, com relógio do banco | Assentamento observado só depois do DELETE não garante que aquele DELETE removeu os últimos bytes |

O candidato é mais conservador antes de qualquer escrita que a promoção. O estado
inicial assentado da promoção só é seguro porque `BeginWritingAsync` exige ausência
de pedido/token de limpeza e de `deleted_at`. Copiar apenas esse valor inicial
para candidatos, sem o protocolo de início correspondente, seria inseguro.

## Contraexemplo reproduzível

`FileSystemObjectStore.PutAsync` copia para `<chave>.<uuid>.partial` e depois executa
`File.Move(..., overwrite: true)`. `DeleteAsync` apaga somente o caminho final.
Não há barreira compartilhada entre esses dois pontos.

1. A geração e o manifesto já existem, com escritor incerto.
2. O PUT copia os bytes para o arquivo temporário e pausa antes do rename.
3. O preparo vence. O coletor apaga as três chaves, inclusive a chave final ainda
   ausente, e confirma uma varredura sem conclusão terminal.
4. Outras instâncias repetem a limpeza. Nem três varreduras nem dois anos de relógio
   falso removem a possibilidade do rename pendente.
5. O PUT retoma e o arquivo final reaparece, com os bytes exatos enviados.
6. Sem o ACK durável do escritor, a nova varredura remove esses bytes mas continua
   conservadora. Se o escritor original finalmente comprovar o término de todo
   seu I/O e que não emitirá outros PUTs, uma varredura posterior pode concluir.

O teste usa o adaptador de arquivos real, uma barreira no fluxo de entrada e o
PostgreSQL real. Não usa sleeps para ordenar a corrida. Os limites de 10 segundos
nas esperas só detectam travamento do teste; não autorizam conclusão de limpeza.
O relógio falso altera elegibilidade de leases/polls, nunca comprova morte.

Também existe uma corrida mais estreita: DELETE da última chave, último PUT,
assentamento e ACK do coletor. A fotografia do claim precisa continuar falsa
nessa varredura, mesmo que o assentamento já seja visível ao concluir. Só a próxima
varredura remove os bytes tardios e registra a conclusão.

## Armazenamento remoto e cancelamento

`S3ObjectStore` envia PUT e DELETE independentes pelo SDK. Não envia precondição de
fechamento, não registra identificadores de operações remotas para reconciliação e
não fornece uma operação de drain/revogação. O timeout HTTP e o cancelamento do
cliente não constituem confirmação durável de encerramento no servidor.

A [documentação do Amazon S3](https://docs.aws.amazon.com/AmazonS3/latest/userguide/Welcome.html#ConsistencyModel)
garante consistência forte para PUT/DELETE, mas não exclusão mútua entre escritores
concorrentes nem atomicidade entre chaves. Isso não transforma DELETE numa proibição
de PUT posterior. Esta conclusão sobre a ausência de fencing é uma inferência da
combinação dessa documentação com as requisições efetivamente enviadas pelo adaptador.

Não extrapolar as garantias da AWS para qualquer endpoint compatível. A documentação
do [Backblaze B2 sobre chamadas sucessivas](https://www.backblaze.com/docs/en/cloud-storage-call-the-s3-compatible-api#successive-calls)
registra ressalvas de ordenação de versões e ocultação dentro do mesmo segundo.
Além disso, [DELETE sem `versionId`](https://www.backblaze.com/apidocs/s3-delete-object)
insere um marcador; a remoção permanente exige a versão. O adaptador atual não passa
essa versão. Ausência numa leitura não prova eliminação de versões históricas.
Nada disso autoriza mudar o bucket de backup, Object Lock ou lifecycle neste trabalho.

Estas observações são documentais, não homologação de S3/B2. Não foram usados conta,
bucket, credencial ou dados de produção.

## Garantia ausente e estratégias possíveis

Para encerrar uma geração incerta é necessário obter uma evidência durável que
ordene **todos os seus possíveis commits de bytes antes da última exclusão** e
impeça novos commits, inclusive de operações já admitidas, escritores pausados e
releases antigas. O fechamento também deve sobreviver a reinício/restore e não
sumir com o expurgo de metadados enquanto alguma capacidade de escrita sobreviver.

São caminhos possíveis, ainda não implementados ou demonstrados de ponta a ponta:

- **Fence imposto no destino.** Uma autoridade de escrita aplica uma revogação
  monotônica por geração e verifica o fence atomicamente com a publicação dos bytes.
  Deve drenar operações admitidas e persistir a revogação antes de responder.
  Escritores não podem contornar essa autoridade com credenciais de PUT direto.
  Marker numa chave separada mais consulta antes do PUT mantém uma corrida.
- **Prova de encerramento dos escritores e do I/O.** Identificar todos os processos,
  tentativas e operações da geração; impedir retomada/reexecução; comprovar término
  e resolver resultados remotos incertos antes de excluir. A morte comprovada do
  processo local termina seu futuro rename, mas não comprova término de um PUT já
  entregue a um servidor remoto. PID reutilizável e ausência de heartbeat não bastam.
- **Protocolo específico para arquivos.** Uma trava entre processos e um registro
  durável de fechamento podem serializar admissão/rename e cleanup sem locks de
  negócio durante I/O. Exigiria protocolo para temporários, crash/fsync, reabertura,
  restore e retirada dos escritores antigos. Não resolve o adaptador S3 e não pode
  ser acrescentado apenas ao coletor.
- **Escrita condicional na mesma autoridade/chave.** Só seria suficiente com uma
  precondição atômica que todo escritor, inclusive antigo, obedecesse e que um
  estado de fechamento não reciclável invalidasse. Create-only seguido de DELETE
  reabre a chave; ETag/HEAD isolados também não fecham uma geração.

Com o contrato e os adaptadores atuais, os históricos “escritor morreu antes de
publicar” e “escritor está pausado antes de publicar” têm o mesmo estado observável
no banco e nas chaves finais. Qualquer regra que encerre ambos apenas pelo tempo
decorrido aceitará o segundo histórico indevidamente. Por isso não há correção
segura que consista apenas em preencher `writer_settled_at` no coletor.

## Regressões e limites da prova

`InterruptedReviewWriterTests.cs` complementa a fixture de
`ReviewPromotionCollectorTests` com quatro teorias, cada uma para candidato e
promoção, sem depender do launcher de sanitização exclusivo do Linux:

| Teste | Evidência |
|---|---|
| `AnInterruptedFilesystemWriteCanReappearAfterRepeatedCleanupAndRecovery` | Arquivo real pausado antes de rename; três limpezas; retomada tardia com bytes exatos; recuperação sem ACK e posterior assentamento explícito |
| `SettlementDuringDeletionRequiresANewSweepBeforeTerminalCompletion` | PUT e assentamento depois do DELETE; varredura iniciada incerta não conclui; varredura seguinte remove e conclui |
| `RecoveryFencesTheOldCollectorButDoesNotSettleTheWriter` | Segundo coletor espera lease; token substituído nega ACK e retry antigos; recuperação mantém escritor incerto |
| `AnUnsettledWriterHoldsAnOldTombstoneAndSettlementStillRequiresTheBackupWindow` | Tombstone de dois anos retido após varredura e após conclusão recente; só sai após 65 dias da remoção no relógio do banco; upload continua `Deleted` |

As linhas de preparo e o ACK do escritor são sintéticos e explícitos. O teste de
retomada modela pausa/interrupção e perda do ACK, não mata um processo de produção
nem simula que PostgreSQL detecta sua morte. Esses testes provam o contraexemplo e
as proteções atuais; não provam encerramento de escritores remotos nem uma solução
de fencing nova. Os testes existentes de `MediaPipelineTests` exercitam os métodos
reais de preparo e a recusa do commit após perda de prazo/autorização; continuam
necessários no CI Linux.

Os metadados e tombstones podem continuar retidos indefinidamente. Temporários de
um processo morto também não são removidos pelo DELETE da chave final. Essa é uma
lacuna de coleta física a considerar no protocolo de arquivos, sem apagar por
idade um temporário que um escritor ainda possa publicar. Preservar exclusão de
`review-candidates` do backup e reconciliação de fotos excluídas no restore; as
promoções em `derivatives` continuam sujeitas à janela de backup documentada.

Rollback desta entrega: reverter apenas documentação e os novos testes. Não há
migração, novo serviço, mudança de permissão ou dado operacional a reverter.

## Validação local da entrega

Ambiente: macOS arm64, SDK .NET 10.0.402, PostgreSQL 18.6 descartável em Docker.
Comandos de backend executados a partir de `backend/`:

| Comando | Resultado |
|---|---|
| `dotnet restore --locked-mode` | Sucesso |
| `dotnet build Duora.slnx --no-restore` | Sucesso, zero erros; três avisos preexistentes de licença do ImageSharp em Debug |
| `dotnet format Duora.slnx --verify-no-changes --no-restore` | Sucesso, sem alterações necessárias |
| `dotnet test --project tests/Duora.UnitTests --no-build --no-restore` | 1.768 aprovados, zero falhas/ignorados |
| `dotnet test --project tests/Duora.ArchitectureTests --no-build --no-restore` | 44 aprovados, zero falhas/ignorados |
| Integração abaixo, após desfazer a mutação | 253 aprovados, zero erros/falhas/ignorados |

```sh
dotnet tests/Duora.IntegrationTests/bin/Debug/net10.0/Duora.IntegrationTests.dll \
  -class Duora.IntegrationTests.Media.ReviewPromotionCollectorTests \
  -class Duora.IntegrationTests.Media.PhotoDeletionRecordPurgeTests \
  -class Duora.IntegrationTests.Media.FileSystemObjectStoreTests \
  -class Duora.IntegrationTests.Safety.PhotoReviewApprovalTests \
  -class Duora.IntegrationTests.Safety.PhotoReviewCleanupLeaseTests
```

A classe do coletor também foi executada isoladamente pelo comando
`dotnet test --project tests/Duora.IntegrationTests --no-restore --filter-class Duora.IntegrationTests.Media.ReviewPromotionCollectorTests`:
11 aprovados, incluindo os oito casos novos.

**Verificação por mutação, desfeita:** passar `true` em vez de
`claim.WriterWasSettled` para o SQL de conclusão nos dois coletores fez os oito
novos casos falharem (os três existentes passaram). Nos dois casos de assentamento
durante DELETE, isso permitiu conclusão prematura. Nos casos ainda incertos, o
predicado SQL adicional impediu o UPDATE, e os testes detectaram a falta da
varredura/recuperação esperada. A rodada final de 253 testes usou os coletores
originais; nenhum arquivo de runtime permanece alterado.

Não executados: suíte de integração completa, cenários de `MediaPipelineTests`
que inicializam o launcher Linux, frontend/e2e e gates remotos de CI. Esta entrega
não modifica runtime nem frontend; esses gates continuam exigidos para integração.
Também não foram executados homologação S3/B2, kill real de processo, restore de
host ou ensaio com releases antigas: não atribuir essas garantias aos testes de
barreira e metadados sintéticos aqui apresentados.

Arquivos alterados:

- `backend/tests/Duora.IntegrationTests/Media/ReviewPromotionCollectorTests.cs`:
  classe passa a `partial` para compartilhar sua fixture de banco/arquivos.
- `backend/tests/Duora.IntegrationTests/Media/InterruptedReviewWriterTests.cs`:
  oito casos novos de regressão e as barreiras reproduzíveis.
- `docs/architecture/adr/0035-revisao-humana-de-fotos.md`: adendo que preserva o
  risco aberto e aponta para a investigação.
- `docs/testing/reports/interrupted-review-writers.md`: esta análise e evidências.

Nenhuma tentativa de instrução não confiável foi detectada no material examinado.
