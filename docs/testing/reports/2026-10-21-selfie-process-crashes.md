# Selfie: recuperação após encerramento de processos

Referência: [issue #93](https://github.com/BipoXDXD/duora/issues/93), em continuidade aos testes do PR #114.
Base validada: `d79321d477362e3d207680bb68b378278febc21b`; código dos testes: `b887e1046af5408b088efe0c5b6997bdae1c5b32`.

## Escopo e isolamento

Cinco cenários executam dois workers em processos do sistema operacional com PIDs distintos,
concorrendo pela mesma outbox em PostgreSQL real via Testcontainers/Docker. O auxiliar compila
a mesma fonte de `SelfieVerificationProviderService` usada em produção e usa os serviços reais
de despacho, persistência e autorização. Cada caso recebe um banco migrado exclusivo com dados sintéticos.

O fornecedor é um servidor HTTP de simulação no loopback do processo pai, que sobrevive à morte
do worker. Recebe apenas identificadores artificiais; nenhuma imagem ou câmera participa do ensaio.
A factory AWS de `ISelfieProvider` é removida antes da construção do contêiner de serviços.
O ambiente do filho é limpo, a configuração padrão do host fica desabilitada e a conexão sintética
é transmitida por stdin, sem argumentos ou logs com credenciais. Banco e fornecedor aceitam apenas
endereços locais. As flags necessárias ao ensaio existem somente na configuração em memória do auxiliar.

Eventos públicos de diagnóstico do EF pausam o worker antes/depois dos commits escolhidos.
Não há novo acesso a internos, hook de produção ou alteração de política arquitetural. O pai confirma
o estado durável por conexão independente, inicia o segundo consumidor e verifica que ele não toma
o lease válido. Em seguida usa `Process.Kill(entireProcessTree: true)`, confirma saída não zero e
novamente a entrega InFlight, evitando aprovar um caminho recuperado por timeout antes da morte.
O segundo processo permanece vivo e recupera a entrega após avanço de 31 segundos no relógio controlado.
PostgreSQL, locks, fencing e commits são reais; a passagem do tempo e o fornecedor são simulados.

## Casos aprovados

| Ponto de interrupção | Estado/versão durável antes da recuperação | Conta durável | Chamadas finais de criação/leitura |
| --- | --- | --- | --- |
| Sessão criada no fornecedor, resposta pendente | Pending / 0 | PendingEligibility | 2 / 1 |
| Antes do commit da sessão | Pending / 0 | PendingEligibility | 2 / 1 |
| Após commit da sessão, antes do reagendamento | Capturing / 1 | PendingEligibility | 1 / 1 |
| Antes do commit do resultado | Capturing / 1 | PendingEligibility | 1 / 2 |
| Após commit do resultado, antes da conclusão da fila | Passed / 2 | Active | 1 / 1 |

Todos terminam com uma tentativa Passed, versão 2, conclusão registrada, conta Active com e-mail
confirmado, elegibilidade liberada e entrega Delivered. Nenhum registro etário é criado. A simulação
mantém uma sessão por tentativa mesmo quando recebe duas chamadas; isso é uma premissa explícita
do fake, não prova de deduplicação ou cobrança no fornecedor real.

## Verificações executadas

- `dotnet test --project backend/tests/Duora.IntegrationTests -- --filter-class '*SelfieProcessCrashTests'`:
  **5 aprovados**, zero falhas/ignorados, após trocar o interceptador por diagnóstico público do EF.
- `dotnet restore backend/Duora.slnx --locked-mode`: aprovado na base atualizada.
- `dotnet test --solution backend/Duora.slnx -- --max-threads 4`: **2.570 aprovados**, zero falhas/ignorados,
  na base atualizada, em Debug/net10.0/arm64; duração de 3m01s. Inclui os cinco casos acima; não somar os totais.
- `dotnet format backend/Duora.slnx --verify-no-changes --no-restore`: aprovado.
- `git diff --check`: aprovado.
- O lockfile do auxiliar reutiliza somente pares pacote/versão já presentes no repositório.

Ambiente: macOS arm64, .NET SDK 10.0.400 e Docker Engine 29.8.2. O aviso preexistente de licença
ImageSharp no build Debug permanece; este relatório não certifica build Release.

A primeira rodada completa encontrou uma violação da regra de visibilidade da infraestrutura.
O acesso adicional foi removido e substituído pelo observador público de diagnóstico; o teste de
arquitetura foi preservado. As duas rodadas completas seguintes passaram (2.568 na base anterior e
2.570 após incorporar a main). Revisão independente não encontrou bloqueios; sua sugestão de conferir
InFlight também depois do encerramento foi aplicada antes dessas aprovações.

## Arquivos alterados

- `backend/tests/Duora.SelfieProcessHost/`: executável auxiliar, configuração isolada, fornecedor loopback,
  observador de commit e lockfile.
- `backend/tests/Duora.IntegrationTests/Identity/SelfieWorkerProcess.cs`: controle e encerramento dos filhos.
- `backend/tests/Duora.IntegrationTests/Identity/LoopbackSelfieServer.cs`: efeito externo simulado compartilhado.
- `backend/tests/Duora.IntegrationTests/Identity/SelfieProcessCrashTests.cs`: cinco cenários e verificações duráveis.
- `backend/tests/Duora.IntegrationTests/Duora.IntegrationTests.csproj` e `backend/Duora.slnx`: build do auxiliar.
- Este relatório. Nenhum comportamento ou configuração funcional da aplicação foi alterado.

## Não executado e pendências

- Workers em containers separados, Windows/Linux operacionais, queda do host, perda de energia,
  indisponibilidade/failover do PostgreSQL e restauração dedicada: este ensaio mata processos locais,
  mantendo o banco disponível.
- Perda de conexão durante COMMIT com resultado desconhecido e falhas da transação Npgsql própria
  de conclusão da outbox. O caso após commit do resultado cobre a retomada lógica desse intervalo,
  sem interromper a rede ou a transação de conclusão.
- AWS real, IAM/STS, TTL, idempotência, cobrança, retenção e eliminação efetivas: fora do escopo autorizado.
- Câmera e selfies reais, calibração/representatividade de qualidade e presença, dispositivos físicos,
  leitores de tela, alternativa assistida e CSP/Permissions-Policy do SDK: não homologados por esses testes.
- DPA, região/transferência, uso secundário, exclusão de conta e backups: pendências de homologação da issue #93.
- Frontend, E2E e scanners integrais não foram repetidos localmente nesta rodada de testes de backend.
  Os gates obrigatórios do GitHub continuam exigidos; nenhuma exceção ou redução foi proposta.

Não houve AWS real, coleta de selfies, implantação ou ativação. A selfie continua desativada nos
perfis da aplicação. Estes resultados não autorizam piloto ou produção; integração e operação
continuam sujeitas à revisão humana. Não foram detectadas tentativas de instrução não confiável.
