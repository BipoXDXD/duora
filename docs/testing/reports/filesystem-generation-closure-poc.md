# Fechamento FileSystem: prova de viabilidade de processos — #311

Ensaio de 01/11/2026. Base: `main` em `40a7df4922c7d2466478a993b744c4a8f2799fa8`.
Branch de trabalho: `codex/311-filesystem-closure-poc`.

## Parecer

O mecanismo cooperativo é demonstrável para processos locais que respeitam a mesma trava e conservam
os controles. **Não há base para habilitar conclusão terminal de escritores incertos no Duora atual.**
O protótipo demonstra recuperação após morte do escritor sem inventar ACK, mas também reproduz quatro
contraexemplos: escritor legado com acesso direto, substituição do inode da trava, remoção do marcador e restauração de
controles antigos. A qualificação completa de host crash, rollout/rollback e restore permanece aberta.

Esta entrega é a etapa experimental B da
[proposta técnica](https://github.com/BipoXDXD/duora/issues/311#issuecomment-6551593250), acompanhada do
inventário necessário à etapa A. Não implementa as etapas C/D (contrato/schema/coletores). É código de
ensaio revisável, não arquitetura aceita ou funcionalidade instalada. Não conclui a
[issue #311](https://github.com/BipoXDXD/duora/issues/311) nem a #69.
Preserva a [investigação do PR #308](interrupted-review-writers.md) e o ADR-0035.

INV-06/08/11/12; T-08/11/16/26; relação com API-M01/M03/M04. Não muda contratos HTTP, autorização,
retenção, montagem, flags, permissões, backup, workflows, serviços ou uploads. Só dados sintéticos.

## Inventário dos caminhos reais e das capacidades

| Caminho versionado | Onde escreve / capacidade | Implicação |
|---|---|---|
| `PhotoReviewCandidates.StageAsync` | Worker: manifesto persistido em `PrepareAsync` antes dos três `IObjectStore.PutAsync` | Nenhum contexto/fence de geração no destino; prazo/cancelamento não revogam PUT já admitido |
| `PhotoReviewPromotions.PrepareAsync` | Lê candidato, `BeginWritingAsync` retira assentamento e depois grava três destinos | Barreira SQL protege início/commit de negócio, não o rename no disco |
| `PhotoReviewApprovals.ApproveAsync` e registro em `PrivatePhotoReviewServiceCollectionExtensions` | Coordenador usado pela Web chama o preparador de promoções | Encerrar somente o Worker ou o sanitizador não cobre os escritores possíveis |
| `MediaProcessingDispatcher` | Sanitização retorna bytes e fluxo chama preparação/candidato | O processo descartável do decoder não é a autoridade que faz PUT no host |
| `FileSystemObjectStore.PutAsync` | Cria `<chave>.<uuid>.partial`, copia, fecha stream e executa `File.Move(overwrite:true)` | DELETE final não drena escritor; classe aceita chave privada pelo método genérico |
| `ReviewCandidateCleanupDispatcher` / `ReviewPromotionCleanupDispatcher` | DELETE de variantes, token/lease e fotografia `WriterWasSettled` no claim | Prova surgida durante DELETE não permite concluir essa varredura; preservado integralmente |
| `DecidedCandidateCleanupDispatcher` e compensações | Também removem chaves via `IObjectStore` | Devem ser inventariados na integração; não transformar decisão do caso em fence de storage |
| `compose.prod.yml` | Web/Worker UID/GID 1654; Web `derivatives` RO, Worker RW; ambos `quarantine` RW | Web tem caminho de código de promoção mas não permissão operacional nessa montagem; não ampliamos mounts |
| `app_dirs/tasks/main.yml` | Áreas `quarantine` e `derivatives` pertencem ao UID 1654, modo 0700 | Um escritor antigo com esse UID e montagem RW pode escrever e trocar nomes/controles que lhe pertençam; chmod isolado não é fronteira contra o próprio dono |
| `review-candidates` | Não provisionado/montado por esse Compose/role | Não assumir topologia privada já homologada; protótipo cria área somente em volume sintético |
| `duora_backup.py` / runbook de backup | Backup de `derivatives`; restore em staging com seleção/reconciliação; gate do ledger externo continua fechado | Controle de revogação não pode ser simplesmente sidecar restaurável junto da mídia; candidatos continuam fora do backup |

O inventário é de código/infra versionados, **não inspeção do host de produção**. Nenhuma conta, dado ou
segredo operacional foi acessado. O PUT genérico também atende quarentena e derivados legados; removê-lo
por completo sem contrato específico quebraria esses usos. A integração futura deve exigir contexto de
geração somente nos caminhos privados, após persistir manifesto e sem locks SQL durante I/O de storage.

## Estado, autoridade e ordenação do protótipo

[Implementação e reprodução](../../../scripts/testing/private-generation-closure/README.md).
O executável compila por link os fontes reais do adaptador, do stream de tamanho exato e das chaves.
A camada experimental serializa a sessão inteira (três variantes) por inode estável com `flock` nativo;
a operação é não bloqueante e `BUSY` nunca significa encerramento.

```text
reserva offline válida + domínio/área/geração
  -> sessão exclusiva: validar -> criar .partial -> copiar -> rename -> dispor todo I/O
  -> fechar: mesma trava -> marcador flush -> rename -> fsync arquivo/diretório -> resposta
  -> varredura: validar fechamento anterior -> DELETE finais/temporários atribuíveis -> fsync área
```

Nenhum arquivo de trava é coletado. Ausência de reserva/trava, corrupção dos registros ou encarnação diferente falham fechado;
`reserve` não reinicializa registro conhecido. O protótipo não resolve admissão SQL nem impede alguém
com acesso direto de apagar/recriar controles. Não aceita uma data, timeout, PID ou cancelamento como prova.
Um processo parado com a trava pode impedir progresso indefinidamente; processo morto libera a posse
somente quando não há descritores duplicados/herdados restantes.

A [semântica de flock](https://man7.org/linux/man-pages/man2/flock.2.html) explica tanto a serialização
quanto o limite advisory e a herança da descrição aberta. A
[semântica de fsync](https://man7.org/linux/man-pages/man2/fsync.2.html) exige sincronizar o diretório além
do arquivo. Os testes conferem chamadas com resultado verificado, mas não a durabilidade do hardware.

Fechamento não é remoção nem ACK SQL. Os resultados do executável não podem satisfazer a constraint atual
que exige `writer_settled_at`. Como a autoridade ainda pode ser contornada, não propomos migração que
aceite esses resultados como prova. Nos coletores reais, escritor incerto continua sem `deleted_at` e
com limpeza repetível. Tombstones mantêm mínimo de um ano e obrigação pendente ou janela de 65 dias após
remoção física; token/lease e relógio do PostgreSQL permanecem intactos.

## Contraexemplos e condições necessárias para avançar

1. **Release antiga.** Pausar `legacy-write` antes do primeiro PUT, fechar e varrer com o protocolo,
   retomar: as três variantes aparecem. É o mesmo `PutAsync`, em processo real UID 1654, sem a nova trava; o helper representa
   o caminho sem fence, não executa um binário histórico de release.
   Não há fence compulsório no método atual. Antes de qualificar, demonstrar retirada de todos os
   escritores antigos e I/O/descritores/descendentes, impedir reexecução/rollback com acesso RW e eliminar
   o bypass genérico de chaves privadas. O ACK do sanitizador não basta.
2. **Inode substituído.** Pausar escritor protegido antes do PUT mantendo a trava; mesmo usuário remove
   e recria o nome `lock`; coletor trava o novo inode e fecha; escritor retoma pela autoridade antiga e
   publica. Proibir criar lock ausente e usar `O_NOFOLLOW` ajuda, mas não impede substituição válida por
   quem pode escrever no diretório. É necessária fronteira de posse/capacidades que preserve controles,
   além do inventário e da retirada dos escritores; código cooperativo sozinho não impõe isso.
3. **Marcador perdido.** Remover somente `closed` preservando a reserva aberta permite gravar outra vez.
   A reserva e a encarnação não atestam que aquele registro nunca foi fechado. Ausência isolada de
   marcador não é detectada como corrupção; requer controles protegidos e reconciliação independente.
4. **Restore regressivo.** Snapshot dos controles abertos; fechar/varrer; restaurar snapshot; mesmo domínio
   e identidade admitem escrita outra vez. Trocar um nome de encarnação em configuração não prova revogação.
   Uma barreira de restore precisa impedir admissão/retomada antiga, conservar/reconciliar revogações fora
   do snapshot regressivo e retirar capacidades antes de liberar leitura/escrita. Sem isso, falhar fechado
   e não concluir. O ensaio de cópia não é homologação do runbook ou do ledger pendente da #69.

Manter controles fora do snapshot de mídia permite negar novos PUTs e repetir a limpeza dos arquivos
restaurados. **A cópia de restore pode repor bytes entre as duas varreduras.** Portanto esse caso não
prova ausência universal de reaparecimento, nem autoriza servir mídia antes da reconciliação.
Não propomos instalar uma nova autoridade/serviço ou alterar permissões de produção nesta entrega.
Uma alternativa a avaliar separadamente é concentrar escrita sob autoridade inacessível aos consumidores;
a alternativa sem novo serviço exige demonstrar quiescência completa, admissão de releases e proteção dos
controles. Nenhuma das duas foi homologada aqui.

## Matriz da proposta

| Cenário | Evidência desta entrega | Limite |
|---|---|---|
| T1 | Pausa antes da trava; fechamento anterior nega PUT sem criar `.partial` | Cooperativo |
| T2 | Pausa EOF antes do rename; três tentativas de close/sweep retornam BUSY, para cada variante | Nenhuma idade/prazo força progresso |
| T3 | SIGKILL antes do PUT, antes do rename e após cada PUT; recuperação remove finais/temporários sem ACK | Crash de processo, não energia/host |
| T4 | Herança intencional mantém BUSY; `/proc` confere CLOEXEC no lock e no arquivo de dados | Não é inventário operacional de todos os processos |
| T5 | Morte após flush, rename e fsync do marcador; retry idempotente; corrupção recusa operações | Perda de gravações não sincronizadas não simulada |
| T6 | Morte após DELETE de cada variante; falha real de permissão de exclusão e falha injetada antes de sync; retry | Sem ACK SQL novo |
| T7 | Dois escritores/fechadores reais; regressões PostgreSQL mantêm fencing de token/lease | Novo fechamento não integrado ao SQL |
| T8 | Oito regressões #308 intactas, incluindo assentamento durante DELETE | Fotografia da nova prova ainda não implementada |
| T9 | Sem fechamento remoto introduzido | Qualificação S3/B2 separada |
| T10 | Três variantes, duas gerações, sentinela de asset legado e temporário não atribuível | Nome arbitrário não é coletado; requer reconciliação |
| T11 | Regressões de aprovação e limpeza/autorização existentes | Novo mecanismo não integrado ao preparo/commit |
| T12 | Reserva/trava ausente ou registros corrompidos/domínio incorreto recusam operações; marcador removido e restore antigo são contraexemplos | Não resolve rollback de todo o domínio |
| T13 | Helper em processo real usa PUT sem fence e recria dados | Nenhum rollout/rollback operacional qualificado |
| T14 | Lock ausente/symlink recusado; substituição por inode regular produz contraexemplo | Diretório gravável pelo mesmo UID não protege a autoridade |
| T15 | Regressões PostgreSQL preservadas, incluindo retenção/expurgo | Nenhuma regra/janela modificada |
| T16 | Snapshot sintético de mídia com controle externo fechado; nova limpeza remove bytes repostos | Restore operacional, ledger e perda de host ausentes |

## Validação e alterações

O runner usa Linux arm64 no Docker Desktop, .NET 10.0.402, UID/GID 1654 e volume de disco descartável
(ext4 na VM). `/tmp` é tmpfs, separado dos arquivos testados. Nenhum diretório operacional é montado.
Reinício de container e de processo conserva kernel/disco da VM. Não é ensaio de energia, NFS ou SMB.
A matriz executa para candidato e promoção, incluindo as três variantes nos pontos de interrupção.
Os testes de banco existentes rodam no host macOS arm64 com PostgreSQL 18.6 real em Testcontainers.

Comandos, resultados finais e mutações são registrados na seção de resultados abaixo. O primeiro ciclo
vermelho mostrou que o PUT tardio era aceito (`esperado 2, recebido 0`) no executável sem proteção, antes
de implementar o protocolo. Os contraexemplos são testes positivos de uma **limitação**, e não casos
omitidos/ignorados. Nenhum teste ou gate existente foi reduzido ou alterado.

Arquivos adicionados: `scripts/testing/private-generation-closure/` (executável experimental, runner,
container restrito, testes e mutações), este relatório e o registro separado de pendências remotas.
Rollback: retirar apenas esses arquivos. Não existe mudança de runtime, schema ou estado operacional.
Não foram detectadas tentativas de instrução não confiável nas fontes consultadas.

## Resultados dos comandos

| Comando | Resultado observado |
|---|---|
| `scripts/testing/private-generation-closure/run_linux.sh` | 23 testes para candidatos e 23 para promoções, todos aprovados; inclui quatro contraexemplos por área. Fechamento/varredura repetidos entre containers diferentes também aprovados |
| `python3 scripts/testing/private-generation-closure/check_mutations.py` | 3 mutações detectadas por falha de asserção; 1 sobrevivente esperado e reportado: omissão de fsync do diretório não é observável por esse teste de processos |
| `dotnet build scripts/testing/private-generation-closure/Probe.csproj` | Sucesso, zero avisos/erros; build também executado dentro da imagem Linux com `RestoreLockedMode=true` |
| `dotnet format scripts/testing/private-generation-closure/Probe.csproj --verify-no-changes --no-restore` | Sucesso |
| `python3 -m py_compile scripts/testing/private-generation-closure/test_protocol.py scripts/testing/private-generation-closure/check_mutations.py` | Sucesso |
| `bash -n scripts/testing/private-generation-closure/run_linux.sh` | Sucesso |
| `dotnet restore --locked-mode` (em `backend/`) | Sucesso |
| `dotnet build Duora.slnx --no-restore` (em `backend/`) | Sucesso, zero erros; 3 avisos preexistentes de licença ImageSharp no Debug |
| `dotnet format backend/Duora.slnx --verify-no-changes --no-restore` | Sucesso |
| `dotnet test --project tests/Duora.UnitTests --no-build --no-restore` (em `backend/`) | 1.768 aprovados, zero falhas/ignorados |
| `dotnet test --project tests/Duora.ArchitectureTests --no-build --no-restore` (em `backend/`) | 44 aprovados, zero falhas/ignorados |
| Integração abaixo (em `backend/`) | 253 aprovados, zero erros/falhas/ignorados, incluindo as oito regressões do PR #308 |

```sh
dotnet tests/Duora.IntegrationTests/bin/Debug/net10.0/Duora.IntegrationTests.dll \
  -class Duora.IntegrationTests.Media.ReviewPromotionCollectorTests \
  -class Duora.IntegrationTests.Media.PhotoDeletionRecordPurgeTests \
  -class Duora.IntegrationTests.Media.FileSystemObjectStoreTests \
  -class Duora.IntegrationTests.Safety.PhotoReviewApprovalTests \
  -class Duora.IntegrationTests.Safety.PhotoReviewCleanupLeaseTests
```

As mutações ocorrem somente em cópias temporárias: não modificam os fontes revisados nem o runtime.
A revisão independente foi estática, sem executar testes; não encontrou problema importante para a entrega
experimental. O ajuste pedido foi precisar que ausência isolada do marcador não equivale a falha fechada;
a redação foi corrigida e um contraexemplo explícito foi acrescentado e executado nas duas áreas.

**Não executados:** suíte de integração completa e preparo/launcher em Linux, matriz nativa amd64,
frontend/e2e, CI remota obrigatória do SHA integrado, restore do runbook em host, interrupção abrupta da VM
ou perda de energia/disco, inventário/retirada real de releases antigas, ensaio de rollout/rollback e
homologação S3/B2. Esta entrega não muda os métodos reais de preparo/limpeza: testa o adaptador por link e
reroda seus coletores existentes com PostgreSQL real. Os limites impedem tratar os resultados como prova
de fechamento aceita pelo banco. CI e revisão humana continuam exigidas para eventual integração.

Próximos passos condicionais: definir a barreira de capacidades/restore com o responsável técnico,
qualificar durabilidade no filesystem/host-alvo e a retirada de escritores sem fence; só depois propor
contrato/schema e integração com preparadores/coletores, preservando captura anterior à última varredura,
posse do coletor e retenção. As [lacunas S3/B2](remote-generation-closure-gaps.md) permanecem independentes.
