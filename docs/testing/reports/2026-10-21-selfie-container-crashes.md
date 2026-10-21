# Selfie: recuperação com workers em containers separados

Continuação da [issue #93](https://github.com/BipoXDXD/duora/issues/93), dos
[processos locais](2026-10-21-selfie-process-crashes.md) e dos testes de COMMIT do
[PR #161](https://github.com/BipoXDXD/duora/pull/161). Somente testes e documentação;
nenhum comportamento, dependência, configuração de produção ou ativação foi acrescentado.

## Isolamento e interrupção

Cada caso usa um banco migrado exclusivo no PostgreSQL 18.6 do fixture e três containers novos:
dois workers e um fornecedor HTTP simulado, todos com IDs distintos do container PostgreSQL.
Os workers têm PIDs distintos no kernel do Docker e executam o auxiliar já existente, que compila
a mesma fonte do consumidor de produção. Não há mudança em sua validação de destinos locais.

A imagem é lida do `FROM ... AS worker` fixado por digest em `infra/images/dotnet/Dockerfile`:
o mesmo runtime .NET do produto, sem shell, SDK ou nova distribuição. O binário de teste e suas
dependências são montados somente para leitura. Containers não privilegiados, root filesystem
somente leitura, capacidades removidas, no-new-privileges e tmpfs limitado; memória de 512 MiB
por worker e 256 MiB para o fake. Esses limites não comprovam capacidade operacional.

Os três compartilham o namespace de rede do banco para usar somente loopback, mantendo processos
e sistemas de arquivos separados. Isso não reproduz isolamento de redes ou hosts de produção.
O fake não usa câmera, imagem ou SDK; uma sessão por attempt ID é uma premissa explícita, não prova
de idempotência ou cobrança real na AWS. O modo novo existe apenas no executável de teste.

Conexões sintéticas entram por stdin, nunca por argumentos Docker ou variáveis do container.
Mensagens de controle têm somente tipo, PID e contagens. O fake expõe somente endpoints locais e
comandos de teste; não transporta dados de participantes ou credenciais reais. A factory AWS do
worker continua removida antes da construção dos serviços. O fixture fornece a configuração e o
ID do seu próprio PostgreSQL; não há descoberta de containers de outra aplicação.

Ambos os workers ficam prontos antes do timeout real do fornecedor. No ponto escolhido, o pai
confere conta, tentativa e InFlight por conexão independente, e comprova que o segundo worker não
toma o lease válido. Em seguida executa `docker kill --signal KILL` no primeiro, conferindo o
label de propriedade, Running=false e ExitCode=137. O teste inspeciona novamente esse estado fora
do helper, evitando aprovar apenas a morte do cliente Docker. O segundo worker e o fake mantêm
o mesmo PID. O banco permanece disponível.

Após avanço de 31 segundos no relógio controlado, o segundo retoma o trabalho. O tempo do lease
é simulado; PostgreSQL, HTTP, containers, encerramento e commits são reais. Ao terminar cada caso,
somente os containers de nomes aleatórios criados pelo teste e seus arquivos CID são removidos.

## Matriz

| Interrupção | Tentativa durável | Conta | Chamadas finais de criação / leitura |
| --- | --- | --- | --- |
| Sessão criada, resposta ainda retida pelo fake | Pending / 0 | PendingEligibility | 2 / 1 |
| Antes de confirmar a sessão | Pending / 0 | PendingEligibility | 2 / 1 |
| Depois de confirmar a sessão | Capturing / 1 | PendingEligibility | 1 / 1 |
| Antes de confirmar o resultado | Capturing / 1 | PendingEligibility | 1 / 2 |
| Depois de confirmar o resultado | Passed / 2 | Active | 1 / 1 |

Todos terminam com uma tentativa Passed/versão 2, conta Active com e-mail confirmado, elegibilidade
e entrega Delivered, sem evidência etária. InFlight é conferido antes e depois do SIGKILL para
distinguir encerramento de timeout/reagendamento. O fake retém a primeira resposta no primeiro
cenário; as demais pausas usam os eventos públicos de diagnóstico do EF já existentes.

## Verificações

- Imagem do runtime obtida pelo digest já fixado no Dockerfile do produto, sem nova dependência.
- Build do auxiliar e integração aprovados em Debug/net10.0; permanece o aviso preexistente de
  licença ImageSharp, que não certifica build Release.
- `dotnet test --project backend/tests/Duora.IntegrationTests --no-build -- --filter-class '*SelfieContainerCrashTests'`:
  cinco aprovados, zero falhas/ignorados, em cerca de 30 segundos.
- Controle negativo: trocar temporariamente o encerramento do container pela morte apenas do
  cliente Docker fez os cinco cenários falharem. Quatro ainda tinham o container Running, e um
  terminou normalmente com código 0; nenhum satisfez SIGKILL/137. Helper restaurado em `finally`.
- A listagem dos recursos com o label exclusivo do teste ficou vazia depois do controle negativo.

- `dotnet test --solution backend/Duora.slnx --no-build -- --max-threads 4`: 2.852 aprovados,
  zero falhas/ignorados, na base `41b5059` com os cinco casos desta fatia.
- `dotnet format backend/Duora.slnx --verify-no-changes --no-restore` e `git diff --check`: aprovados.
- Revisão independente dos sete arquivos: nenhum bloqueador encontrado; revisão estática,
  sem executar Docker ou substituir os resultados das suítes.

Os nove gates do commit final continuam exigidos antes de declarar esta entrega integrada.
A combinação com o PR #161 (main `bf97bbd`) foi conferida em árvore isolada. Os 23 casos focados
(S3 e recuperação de COMMIT/container) passaram, zero ignorados. A primeira execução completa conjunta
apresentou duas falhas de transporte S3 (connection reset), foi interrompida e será repetida com menor
concorrência local; não há alegação de aprovação dessa rodada. Nenhum código de storage ou teste foi
relaxado. A suíte completa e os nove checks continuam necessários na árvore final.

## Arquivos e reversão

- `SelfieContainerCrashTests.cs`: cinco cenários, inspeção independente e verificação da recuperação.
- `SelfieWorkerProcess.Containers.cs`: execução Docker, IPC, propriedade, inspeção e encerramento.
- `SelfieWorkerProcess.cs`: mesma interface para processos locais; suporte adicional à identidade
  do PID dentro do container, remoção do recurso próprio e campos opcionais do fake.
- `PostgresFixture.cs`: exposição interna do ID do container descartável já criado pelo fixture.
- `Duora.SelfieProcessHost/Program.cs` e `SyntheticProviderHost.cs`: modo de fornecedor sintético
  local, sem adicionar framework ASP.NET ao auxiliar.
- Este relatório. Os primeiros quatro arquivos ficam em `backend/tests/Duora.IntegrationTests`;
  os helpers de identidade ficam no subdiretório `Identity`.

Reverter esses arquivos remove somente a cobertura adicional e preserva o consumidor, os dados,
o schema e o comportamento funcional. Nenhuma alteração de regra arquitetural ou workflow.

## Não comprovado

Não houve queda física do host/energia, failover do PostgreSQL, restauração dedicada do fluxo ou
execução de workers Windows. Os testes de conexão perdida durante COMMIT têm entrega própria no
PR #161, sem confundir seus resultados com os desta matriz.

AWS/câmera reais, IAM/STS, TTL, cobrança, retenção e exclusão reais, qualidade representativa,
dispositivos, leitores de tela, alternativa assistida e CSP/Permissions-Policy continuam sujeitos
às homologações da #93. O mesmo vale para os requisitos operacionais de privacidade e recuperação.
Nenhum deploy, migração operacional ou ativação. A #93 permanece aberta.
