# #311 — evidência da fronteira experimental, PR A

Data: 01/11/2026. Base: `19f4678` (main); continuidade do PR #316 / `b915b10`.
Escopo: tarefas 1–2 do plano aprovado. ADR-0045 permanece **Proposed**.
**O fechamento terminal para escritores incertos não foi habilitado. A issue #311 permanece aberta.**

## Resultado delimitado

O novo executável de ensaio usa os fontes atuais do FileSystem por link e demonstra uma fronteira
Linux entre cliente/legado, autoridade e controlador. São processos/volumes descartáveis com UIDs
distintos; não representam alteração de identidades/mounts/serviços do Duora. Somente o ensaio tem
respostas `ClosedExperimental` e `SweptExperimental`. Não existe consumidor dessas respostas no backend.

A autoridade pode gravar o journal root:17010/0660, mas não pode trocar nomes sob pai root:17010/0750.
Clientes 1654 e 17020 não alteram controles ou bytes privados mesmo com mount RW. O controlador
17030 guarda sua referência em segundo volume inacessível à autoridade/clientes. Conexões usam
sockets 0666 intencionalmente: autenticação de UID/GID pelo kernel nas duas direções, sem conceder
um grupo compartilhado. Pais provisionados impedem substituição do endpoint por clientes.

Enrollment root:grupo/0440 ancora domínio/época/versão, reservas sintéticas e dispositivos/inodes.
`openat`/`O_NOFOLLOW`/`O_CLOEXEC` e `statx` verificam os objetos abertos. Os atores têm CapEff=0 e
NoNewPrivs=1. O provisionador tem CAP_CHOWN apenas dentro dos volumes exclusivos, sem DAC_OVERRIDE.

Journal encadeado exige `Reserved -> Open -> Closing -> Closed`. Falta/corrupção/cauda truncada
bloqueia operações sem recriação. Exclusão global cobre o I/O até EOF/dispose/rename. Fechamento
ordena Closing sincronizado, revogação sincronizada confirmada, Closed sincronizado e só então
resposta. Retry sincroniza de novo registros existentes; erro não é confirmação. Varredura exige
Closed e revisão confirmada, remove somente as chaves/temporários exatos e sincroniza o diretório.
Nomes desconhecidos ficam para reconciliação; a resposta não prova ausência universal de bytes.

## Reprodução e resultados

Ambiente de ensaio: Docker Linux **aarch64 nativo**, volumes locais do Docker Desktop; SDK 10.0.402,
base arm64 `sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa`.
O runner registra SHA/árvore suja, hash das fontes, ID imutável da imagem, arquitetura, SDK e exit code.
Fontes finais, comandos e resultados estão vinculados ao diff deste PR; não a uma imagem de produção.

| Comando na raiz | Resultado |
|---|---|
| `python3 scripts/testing/private-generation-closure/boundary/run.py --report-dir /tmp/duora-boundary-report` | 27 testes pass (incluem cenários parametrizados nas duas áreas), zero ignorados |
| `python3 scripts/testing/private-generation-closure/boundary/check_mutations.py` | 3 mutantes detectados por asserções comportamentais; nenhum erro de build/setup contado como detecção |
| `scripts/testing/private-generation-closure/run_linux.sh` | 23 candidate + 23 promotion e restart entre containers: PASS |
| `python3 scripts/testing/private-generation-closure/check_mutations.py` | 3 detectados; omissão de sync sobrevive, como no #316 |
| `dotnet restore backend/Duora.slnx --locked-mode` e `dotnet build backend/Duora.slnx --no-restore` | PASS; 3 avisos já existentes de licença ImageSharp no build Debug, nenhum erro |
| `dotnet format backend/Duora.slnx --verify-no-changes --no-restore` | PASS |
| `dotnet test --project backend/tests/Duora.UnitTests --no-build` | 1.768 pass, zero ignorados |
| `dotnet test --project backend/tests/Duora.ArchitectureTests --no-build` | 44 pass, zero ignorados |
| Integração filtrada, comando abaixo | 253 pass com PostgreSQL real, zero ignorados |
| Build dos dois projetos de ensaio, format verify do novo e `python3 -m compileall -q scripts/testing/private-generation-closure/boundary` | PASS |

```sh
dotnet test --project backend/tests/Duora.IntegrationTests --no-build -- --filter 'FullyQualifiedName~ReviewPromotionCollectorTests|FullyQualifiedName~PhotoDeletionRecordPurgeTests|FullyQualifiedName~FileSystemObjectStoreTests|FullyQualifiedName~Safety.PhotoReviewApprovalTests|FullyQualifiedName~Safety.PhotoReviewCleanupLeaseTests'
```

A integração selecionada preserva #308, aprovação, posse/lease do coletor, exclusão e retenção.
A nova fixture **não roda automaticamente na CI geral**: o experimento foi executado manualmente,
sem mudar workflows ou gates. A CI do PR é evidência adicional das regressões, não homologação do
mecanismo. A suíte completa de integração, frontend/e2e e scanners não foi repetida localmente:
o diff não altera essas áreas; seus gates existentes continuam obrigatórios antes de eventual merge.

## Casos e sensibilidade

- Syscalls exatos unlink/rename/truncate/overwrite/chmod sobre lock/journal: EACCES/EPERM e
  inode/hash/modo/dono intactos. Autoridade também não troca nome/chmod do lock.
- Mesmo grant válido entregue ao UID legado é recusado. Campos de UID, versão antiga, caminhos,
  área/nonce/geração inválidos, frames grandes e corpo truncado/excedente não publicam bytes.
  Cliente/legado/controlador não podem fechar a referência; cliente não fecha nem varre geração.
- Falta, troca de dono/modo/inode, hardlink, symlink de arquivo/pai, journal vazio/corrompido/truncado
  não resultam em Open. Remover um sidecar legado não apaga o Closed explícito.
- Pausa na sonda EOF do adaptador, antes do rename, nas três variantes das duas áreas: fechamento
  e escritor concorrentes recebem Busy. `/proc` confirma CLOEXEC nos descritores de lock e dados.
- SIGKILL real antes do PUT, na sonda EOF e depois do PUT nas duas áreas. Pai observa exit 137;
  recuperação não depende de ACK do escritor. Não há processo de escrita destacado pela autoridade.
- SIGKILL após Closing sincronizado, confirmação da referência e Closed sincronizado, nas duas
  áreas; retry idempotente. Controlador morto após persistir e resposta perdida da autoridade
  não geram confirmação falsa. A revisão confirmada permanece estável.
- Fechamento/varredura/tentativa tardia após reinício, dois fechadores e escritor sob a mesma
  exclusão, geração vizinha e sentinela committed intactas. Varredura sem Closed é recusada.
- SIGKILL após cada DELETE nas duas áreas; repetir varredura não permite nova escrita. Nome
  desconhecido é preservado; temporário exato órfão é removido. Erro real de File.Delete (diretório
  no nome final) e falhas injetadas de sync não produzem resposta de sucesso.

RED observado antes da implementação: unlink legado efetivamente removia o controle; PUT autorizado
retornava Denied; fechamento ainda não implementado retornava Denied. Um RED adicional mostrou que
retry de Closed retornava confirmação apesar de erro de sync injetado; a implementação agora exige
re-sync. Falhas de setup/compilação não foram tratadas como RED de garantia.

Mutantes novos: pai de controle gravável; admitir peer legado com grant válido; liberar lock antes
do I/O. Todos detectados pelo teste correspondente. A rodada inicial ampliada também encontrou
bugs na fixture (corpo enviado seguia tamanho declarado e inspeção usava UID sem travessia); foram
corrigidos sem relaxar asserções ou conceder capabilities adicionais.

## Contraexemplos e garantias ainda ausentes

Os 46 testes originais e os quatro contraexemplos do #316 continuam íntegros: escritor sem fence,
troca de inode da trava, remoção do marcador e restore de controles abertos. A nova proteção nega
operações de novos peers sem capacidades dentro da fixture; **não revoga um descritor já aberto**
por escritor antigo nem comprova quiescência do host. Nenhum binário histórico foi executado aqui.

A fase A usa enrollment finito de uma época, sem reserva dinâmica. Restart na mesma época é permitido
para exercitar recuperação do journal; **isso não é readmissão segura após restart/restore**. PR B
precisa demonstrar retirada da família/capacidades antigas, política de lançamento de executáveis
aprovados, aposentadoria de época e staging/reconciliação antes de servir dados restaurados. UID
novo/chown/mount trocado não são prova de revogação de FDs anteriores.

O segundo volume é só uma referência local separada, não custódia externa #69/#173. Perda/corrupção
bloqueia; não há reset automático. A autoridade confiável ainda grava conteúdo do próprio journal.
Administrador, provisionador ou autoridade comprometidos continuam fora da garantia. Rollback de
**todo** o universo de confiança não é detectável aqui. Não usar este PR como autorização de restore.

SIGKILL/restart de processo/container conserva kernel e page cache. O mutante original sem sync
sobrevive: isso demonstra o limite do teste, não que sync seja dispensável. Falhas injetadas verificam
propagação de erro; faltam perda de energia, crash do host/VM, hardware/filesystem alvo, NFS/SMB e
qualificação nativa amd64. Imagem multiarch/serviço saudável não substitui essas provas.

S3/B2 continuam separados: pedidos aceitos com resposta perdida/commit tardio, multipart e abort,
retry, versionamento/delete markers, lifecycle, credenciais e garantias de exclusão terminal exigem
prova específica. Não há novo resultado remoto neste PR; ver
[pendências remotas](remote-generation-closure-gaps.md).

## Alterações e decisão

Arquivos novos: executável/harness/testes/Dockerfile/README em
`scripts/testing/private-generation-closure/boundary/`, ADR-0045 e este relatório; índice de ADRs
atualizado. O único ajuste no baseline é `Compile Remove="boundary/**/*.cs"` em Probe.csproj para
impedir inclusão recursiva do segundo executável; fontes/testes anteriores permanecem intactos.
Nenhuma mudança em runtime, SQL, schema, retenção de um ano/65 dias, relógio do banco ou posse/lease.
Limpeza de escritor incerto permanece repetível e não terminal; `writer_settled_at` e `deleted_at`
não são preenchidos por esta prova. Nenhuma mudança de produção, upload, backup ou workflow.

Decisão: PR experimental em rascunho, sem autorização de avanço operacional. #311/#69 permanecem
abertas; terminalidade depende de novas provas e integração revisada separadamente. Reversão retira
somente os arquivos experimentais/documentais e a exclusão do projeto pai. Não foram detectadas
instruções não confiáveis tentando ampliar o escopo.
