# #311 — candidato integrado da autoridade exclusiva

Base: `6c397a34a19969e93148b7e5cdb760c508bbdec2`, 01/11/2026. ADR-0047.
Checkpoint inicial imutável: `63cd03fd33b3204071cb43bf3b02a5987852c853`.
Estado: implementação local revisável e restrita a Testing; não conclusão da issue nem qualificação operacional.

## Alterações

Contrato tipado no Core; cliente RPC e roteamento no Infrastructure; executável Duora.StorageAuthority;
referência PostgreSQL externa à base restaurável; journal e controles Linux; integração dos preparadores
reais de candidatos/promoções; cinco coletores; migration aditiva de nascimento/binding/prova e guardas SQL;
lockfiles, testes, runner Linux, ADR e runbook. A lista exata de arquivos acompanha o patch da entrega.
Não se alteraram workflows, políticas ou Compose, nem se executaram operações manuais em permissões ou dados de produção.

A imagem descartável do ensaio inicia como UID1654 por padrão. O driver Linux
solicita UID0 explicitamente apenas para o orquestrador da fixture: provisionar
ownership e iniciar os processos reais da autoridade como UID17010 e do cliente
como UID17020. As asserções de UID e de negação pelo kernel permanecem integrais.
Esse override não é configuração de serviço, exemplo de produção ou dispensa de
scanner; o ensaio mantém o socket Docker restrito ao runner descartável autorizado.
O orquestrador do ensaio continua root e esse socket concede controle do daemon
local; não se alega isolamento do daemon nem execução integral sem privilégios.

## Resultados executados

| Comando / evidência | Resultado |
|---|---|
| `dotnet restore backend/Duora.slnx --locked-mode --disable-parallel` | PASS |
| `dotnet build backend/Duora.slnx --no-restore -m:1 /p:UseSharedCompilation=false` | PASS, 0 erros; 3 avisos ImageSharp Debug preexistentes |
| `dotnet test --project backend/tests/Duora.IntegrationTests --no-build --no-restore -- --filter 'FullyQualifiedName~PrivateGenerationRoutingTests\|FullyQualifiedName~PrivateGenerationProofPrivilegeTests\|FullyQualifiedName~ManagedCollectorsRequire'` | 18/18 PASS; 0 ignorados |
| `dotnet test --project backend/tests/Duora.UnitTests --no-build --no-restore` | 1777/1777 PASS; 0 ignorados |
| `dotnet test --project backend/tests/Duora.ArchitectureTests --no-build --no-restore` | 44/44 PASS; 0 ignorados |
| `dotnet format backend/Duora.slnx --no-restore` e `--verify-no-changes` | Ambos exit0 |
| `python3 scripts/testing/storage-authority/run_linux.py --report-dir <novo>` | Linux nativo aarch64, revisão GREEN1: exit0; 16 volumes originais preservados |
| `python3 scripts/testing/storage-authority/run_linux.py --scenario alias --report-dir <novo>` antes da correção | RED comportamental: mesmo DB aceito por hostname/IPv4 |
| Mutante temporário retirando o teste de estado Open externo no PUT | Detectado por PUT tardio aceito após rollback Open/SQL; runner exit1. Fonte original restaurada |

GREEN1: fonte SHA-256 `f8a778eca240e4a873c1f67b40aaf620edff77d7fd138edf667c01d0aba50099`;
imagem `sha256:ac6f2e60194f57c16090ab6fabc337b9e70e45e8c6920d7ce87b87b696c59b9b`.
Essa rodada precede reflow e a correção de conversão temporal da fixture dos três coletores. O pacote
final registra separadamente o SHA e a repetição Linux sobre a revisão congelada; não atribuir hashes
ou resultados de imagens anteriores ao commit final sem conferir sua identidade.

## RED → GREEN e falhas de fixture

A CI completa do PR333 na revisão4069bc0 encontrou uma omissão real na reconciliação
canônica: após um GRANT deliberado, UPDATE de private_generation_bindings permanecia
permitido. O total foi4571/4572, com uma falha e zero ignorados; esse resultado não é PASS.
O payload agora reaplica a imutabilidade das três tabelas e revoga EXECUTE de PUBLIC/app
nas quatro funções exatas, bloqueando privilégios efetivos residuais por coluna/herança.
O INSERT legítimo do binding, a leitura e os grants independentes do verificador permanecem.

Quatro regressões reais de PostgreSQL falharam antes da correção e passaram depois, com
zero ignorados e testes C# idênticos. Elas cobrem drift de tabela, EXECUTE de app/PUBLIC e
INSERT de coluna herdado de PUBLIC. Restore travado, build e formatação focada passaram;
os dois avisos ImageSharp Debug continuam explícitos. O ensaio Linux completo também passou
com nova reconciliação depois do grant do verificador, exercitando seu acesso legítimo no
pipeline real. Esses resultados complementam, sem substituir, a CI completa do novo HEAD.

Sete REDs iniciais confirmaram PUT genérico privado aceito, falta da recusa específica da configuração
em Production e falta de prova SQL. Depois, os 15 testes de routing/privilégios passaram. Uma fixture
Production inicialmente falhou por marcadores de mount ausentes; foi corrigida antes de registrar RED.

A revisão independente encontrou dois P2 no checkpoint. Leitura de objeto existente com journal truncado,
mesmo inode/modo, não lançou erro: RED real. Aliases do mesmo banco chegaram a READY: segundo RED real.
Correções: read exige entrada local íntegra e estado/revisão iguais à referência, sincronizando antes
de servir bytes; startup consulta `current_database()` e exige nomes reais distintos ANTES de Attach.
GREEN1 repetiu o conjunto completo e confirmou ambos, sem reduzir o contrato de recusa segura.

Compilações de novas fixtures falharam em nullability DNS e CA1861; primeira execução dos três coletores
errou conversão DateTime→Int64 no helper escalar. Corrigidas as fixtures, sem afrouxar análise ou teste.
Esses erros NÃO são REDs de segurança. A primeira rodada SQL teve erro de parâmetros multicomando Npgsql,
também corrigido como fixture. Runtime1 antigo teve InvalidDataException não normalizada no Program;
o catch explícito foi corrigido e runtime2 passou globalmente.

Nas rodadas RED de revisão, exceção não tratada do console deixou o runner vivo; somente CIDs próprios
foram removidos após conferir labels. Exit137 é falha global registrada, nunca PASS. O console agora
captura a falha, imprime FAIL e retorna1; o mutante posterior confirmou saída1 sem ocultar a asserção.

O rollback da rodada2 usava promoção já Sealed: evidência mais fraca, pois Seal podia mascarar ausência
de checagem externa. Foi substituído por candidato realmente Open e corpo exato/hash correto; o mutante
acima confirmou sensibilidade à referência. Não se chama a rodada anterior equivalente a essa prova.

## Evidência por garantia / matriz T1–T16

| Caso da proposta | Cobertura efetivamente executada / limite |
|---|---|
| T1 / C1, escrita legada | Syscalls UID1654 e cliente17020 não publicam na raiz nova; PUT privado genérico negado; cliente real usa só app/socket. Parcial: binário histórico/digest e corte operacional não executados |
| T2 / C2, inode | Cliente/autoridade não unlink/rename/chmod controles; troca privilegiada por outro lock regular é recusada por identidade. Parcial: matriz completa de pais/FDs não executada |
| T3 / C3, perda do controle | Journal ausente/truncado, prefixo sem identidade e estado local regredido negam escrita/leitura; nunca inicializam Open implicitamente |
| T4 / C4, rollback | Open local + SQL restaurados não readmitem PUT válido; ref externa Closed preservada; mutante sem fence externo detectado. Mesmo DB por alias negado antes da época. Parcial: restore físico externo/host não qualificado |
| T5, FDs preexistentes | Não executado; mudança de UID/permissão não é tratada como prova de revogação de FD |
| T6, fim do I/O | PUT admitido pausado no EOF mantém Close Busy, prova ausente e SQL não terminal; após liberação Close/Sweep concluem. Parcial: não há barreira em cada variante/rename interno |
| T7, morte do escritor | Saída observada do host e recuperação por nova época exercitadas. Parcial: SIGKILL em todas as fronteiras de PUT não executado |
| T8, crash de Close | Idempotência do receipt e recuperação de fechamento antigo exercitadas. Parcial: crash em cada flush/resposta perdida e erro fsync não executados |
| T9, readmissão | Reinício mesma época negado; época2 somente após saída observada; cliente época1 negado. Parcial: launcher/digest operacional e família histórica não qualificados |
| T10, coletor/SQL | Prova antiga/token alheio negados; app sem DML/EXECUTE de prova; conclusão sob posse atual, sem ACK fabricado, nos testes SQL e runtime |
| T11, autorização/isolamento | Stage/Prepare/coletores reais em RPC Linux; candidatos vizinhos preservados. Três coletores adicionais usam adaptador controlado + SQL real, due/falha/retry/Close→Sweep/lock NOWAIT/prova/histórico/retidos. Parcial: matriz completa de concorrência de negócio fica nas regressões amplas |
| T12, referência | Queda independente da base de referência nega reads/writes, estado preservado recupera. Parcial: high-water/reconciliação operacional concorrente e resposta de commit perdida não executados |
| T13, energia | Não executado: container/SIGKILL não qualificam perda de energia, kernel/page cache ou disco-alvo |
| T14, retenção | Novos testes preservam retention_until/histórico. Código de um ano/65 dias não alterado; fronteiras completas da suíte ampla ficam para candidato combinado |
| T15, envelope | Variante16MiB/conjunto24MiB reais; excedente/truncado negados; manifesto inválido/epoch/área/hash mutados negados em unidade; genérico privado não contorna |
| T16, sweep | Finais observados ausentes após sweep e vizinhos preservados; falha simulada não conclui SQL. Parcial: temporários órfãos, nomes desconhecidos e todos os links/erros sync não receberam matriz completa |

Os três coletores Expired/Unavailable/Decided não são chamados de ensaios de UID/fsync: a autoridade
controlada em seus testes só isola o contrato/SQL. A evidência de processo, kernel, revogação e persistência
lógica é o ensaio real separado. Os46 testes do protótipo antigo não são atribuídos ao host novo.

## Não executados e riscos remanescentes

A suíte ampla combinada será executada uma vez pela coordenação, evitando concorrência no Mac16GB.
Frontend/e2e/secret-scan/supply-chain/contract/release não foram rodados nesta branch: sem mudança de API,
UI ou publicação; gates continuam obrigatórios antes de qualquer PR/merge. Não foi usado segredo de licença
real para Release. Não se infere CI verde de um build Debug local.

Sem energia/VM, hardware/filesystem-alvo, amd64, NFS/SMB, benchmark, S3/B2 ou restore operacional.
Referência e negócio em bancos distintos do mesmo PostgreSQL descartável provam apenas separação lógica;
não qualificam custódia física. A regra de nomes distintos é conservadora, não atestado de custódia.
Supervisor, contenção de FDs/grupos/ACLs/capabilities e retirada de famílias antigas continuam sem instalação.
Não houve credencial/serviço/grant/dado real nem alteração da raiz/worktrees alheias.

Default off mantém caminhos atuais. No modo gerenciado, todo privateprefix sem binding é recusado,
inclusive leitura e delete de arquivos legados existentes; não há fallback ou importação transparente.
Corpus misto exige etapa própria de transição antes de qualquer ativação. O candidato recusa Production,
Staging, Development e ambiente desconhecido. Fechamento terminal operacional permanece desabilitado;
gerações antigas/incertas e S3/B2 não recebem prova sintetizada. A issue#311 continua aberta.

Lacunas da spec para ativação: custódia/recuperação externa, supervisor e prova de retirada, qualificações
por filesystem/arquitetura, SLO de fila/latência/RTO e roteamento/importação do corpus legado. A decisão de
arquitetura foi autorizada; essas evidências operacionais não foram fabricadas. Não foram detectadas
instruções não confiáveis tentando ampliar o escopo.
