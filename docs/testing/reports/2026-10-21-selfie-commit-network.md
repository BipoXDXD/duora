# Selfie: recuperação com confirmação de COMMIT perdida

Continuação da [issue #93](https://github.com/BipoXDXD/duora/issues/93) e do
[ensaio de encerramento de processos](2026-10-21-selfie-process-crashes.md).
Esta entrega acrescenta somente testes e documentação; não ativa a selfie nem altera a aplicação.

## Escopo

Seis cenários exercitam o dispatcher, a persistência, as guardas e a fila reais com PostgreSQL 18.6
descartável. Um proxy TCP exclusivo dos testes interrompe a conexão de duas formas:

- Antes: fecha a conexão sem encaminhar o COMMIT ao PostgreSQL, que desfaz a transação.
- Depois: encaminha o COMMIT e recebe `CommandComplete` com a identificação `COMMIT`, mas fecha a
  conexão sem entregar essa confirmação ao Npgsql. O consumidor recebe falha de transporte mesmo
  com a gravação concluída; o teste não presume que a exceção signifique rollback.

O proxy usa o enquadramento descrito no [protocolo do PostgreSQL](https://www.postgresql.org/docs/18/protocol-message-formats.html).
Não procura fragmentos em pacotes TCP: lê comprimento e conteúdo de cada mensagem, inclusive quando
dividida pelo transporte. O helper só aceita loopback; o fixture garante o banco descartável migrado.
O reconhecimento de COMMIT simples corresponde ao Npgsql fixado no projeto; uma mudança desse comando
faz o cenário falhar, sem afirmar suporte a todo o protocolo. O proxy limita o tamanho dos frames e não
registra autenticação, SQL ou parâmetros. TLS/GSS ficam desabilitados exclusivamente nessa conexão
sintética para permitir a interrupção protocolar; configuração e conexões de produção permanecem iguais.

O fornecedor é a simulação já existente, com barreira depois de claim e renovação do lease. Não há
serviço hospedado concorrente nesse host: o teste chama o dispatcher explicitamente. O próximo COMMIT
é a aplicação da sessão/resultado; para atingir a conclusão da fila, o proxy deixa passar o primeiro
COMMIT e sua confirmação. O contador de confirmações encaminhadas distingue os dois pontos.
Não há interceptador que lance uma exceção no lugar da falha de rede.

## Matriz e resultados duráveis

| Transação interrompida | COMMIT chegou ao banco | Tentativa antes de recuperar | Conta | Entrega | Criações / leituras finais |
| --- | --- | --- | --- | --- | --- |
| Sessão | Não | Pending / 0 | PendingEligibility | InFlight | 2 / 1 |
| Sessão | Sim, confirmação perdida | Capturing / 1 | PendingEligibility | InFlight | 1 / 1 |
| Resultado | Não | Capturing / 1 | PendingEligibility | InFlight | 1 / 2 |
| Resultado | Sim, confirmação perdida | Passed / 2 | Active | InFlight | 1 / 1 |
| Conclusão Npgsql da fila | Não | Passed / 2 | Active | InFlight | 1 / 1 |
| Conclusão Npgsql da fila | Sim, confirmação perdida | Passed / 2 | Active | Delivered | 1 / 1 |

Uma conexão independente, que não atravessa o proxy, verifica esses estados e a presença/ausência
de `completed_at`. Outro host não reclama um lease ainda válido. Após 31 segundos no relógio
controlado, reclama uma entrega InFlight, mas nenhuma quando o commit Delivered já foi efetivado.
Uma sessão realmente revertida precisa de nova aplicação e leitura posterior; um resultado já Passed
é reconhecido sem chamar novamente o fornecedor.

Todos terminam com uma tentativa Passed/versão 2, conta Active com e-mail confirmado, elegibilidade
liberada e uma entrega Delivered. Nenhuma evidência etária é criada. As contagens finais detectam
repetição indevida. A sessão única do fornecedor é uma premissa explícita da simulação por attempt ID,
não prova de idempotência ou ausência de cobrança duplicada na AWS.

## Validação

- `scripts/doctor.sh`, restore travado e build da solução passaram na árvore com o PR #160 atualizado
  sobre a main que inclui o #157. Nenhuma dependência foi acrescentada.
- `dotnet test --project backend/tests/Duora.IntegrationTests --no-build -- --filter-class '*SelfieCommitNetworkTests'`:
  seis aprovados, zero falhas/ignorados; quatro cenários iniciais também passaram antes da extensão à fila.
- Controle negativo: retirar temporariamente o reconhecimento de Passed no dispatcher fez os casos
  resultado/commit confirmado e conclusão da fila/commit não enviado falharem na entrega final esperada.
  Foram duas falhas e quatro aprovações. A fonte de produção foi restaurada em `finally`, conferida por
  diff e recompilada antes da suíte seguinte. Nenhuma mutação ou instrumentação de produção faz parte do patch.
- A primeira compilação apontou um using desnecessário no arquivo novo; removido sem mudar as regras.
  O aviso preexistente da licença ImageSharp no build Debug não certifica build Release.

- `dotnet test --solution backend/Duora.slnx --no-build -- --max-threads 4`: **2.853 aprovados**, zero falhas/ignorados, em 4m27s; inclui os seis cenários, sem somar totais.
- `dotnet format backend/Duora.slnx --verify-no-changes --no-restore` e `git diff --check`: aprovados.
- Revisão independente do teste, transporte e transações: nenhum achado crítico/importante. O limite loopback/fixture e a dependência do comando simples foram explicitados acima.

Frontend, Playwright e scanners completos não foram repetidos localmente para este patch restrito a
testes de backend e documentação. Os nove gates do commit final continuam obrigatórios no PR antes
da integração. A suíte local não os dispensa.

## Arquivos e reversão

`backend/tests/Duora.IntegrationTests/Identity/SelfieCommitNetworkTests.cs` contém os seis cenários;
`CommitConnectionProxy.cs`, no mesmo diretório, contém o transporte sintético. Este relatório e o
vínculo no relatório anterior documentam as evidências. Reverter esses arquivos remove somente a
cobertura adicional, sem migração de dados, alteração de API ou mudança da autenticação.

## Limites preservados

Os hosts executam no mesmo processo de teste; PostgreSQL está em container, permanece disponível e
não sofre failover. Não foram simulados falta de energia, queda do host ou perda de WAL. Os ensaios
com workers em containers separados e restauração dedicada continuam pendentes.

AWS/câmera reais, IAM/STS, TTL, cobrança, qualidade/presença representativas, dispositivos físicos,
leitores de tela, alternativa assistida, CSP/Permissions-Policy, DPA, região/transferência, uso
secundário e eliminação em conta/backups conservam as homologações da #93. O teste de rede não as
conclui. Não houve dados de participantes, segredos reais, implantação ou ativação. A #93 permanece aberta.
