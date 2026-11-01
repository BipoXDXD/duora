# Observador de continuidade — investigação da issue #326

## Evidência original e limites da conclusão

Na publicação do PR #323, o [job arm64 da tentativa 1](https://github.com/BipoXDXD/duora/actions/runs/41294826228/job/124813429169)
falhou no teste `RuntimeContinuityLabTests.test_real_two_deploy_continuity_guards_installer_and_compatible_rollback`.
O erro foi `database:TimeoutExpired`, com uma amostra, fluxo de eventos vivo e janela de aproximadamente 49 s.
O commit foi `96cd4bdf3ba3eaabc9b45b5fe2c91453f04ff709`.

O [mesmo teste passou na tentativa 2](https://github.com/BipoXDXD/duora/actions/runs/41294826228/job/124815709594),
sem mudança de commit. O relatório dessa execução contém 149 amostras em cerca de 46,54 s, com lacuna máxima
de aproximadamente 0,334 s. Isso comprova o resultado daquela repetição; a causa específica da demora anterior
não foi registrada. Não há evidência suficiente para atribuí-la a CPU, Docker ou indisponibilidade do banco.

## Mecanismo reproduzido

O sampler anterior executava `docker exec ... pg_isready` a cada amostra, com `subprocess.run(timeout=2)`.
Esse prazo incluía criar o processo/transporte e executar o probe. Qualquer exceção encerrava o sampler e
registrava apenas o tipo, sem fase ou duração. O timestamp era obtido antes da execução.

Uma reprodução portátil executou a função `sample` original extraída para um contexto isolado: primeira chamada
imediata; segunda inicialização de transporte atrasada em 2,1 s, com comando interno que terminaria em sucesso.
Resultado observado: **uma amostra**, `database:TimeoutExpired`, aproximadamente **2,28 s** de execução e rejeição
do gate. Esse experimento usa processos reais controlados, sem Docker/PostgreSQL; demonstra o mecanismo, sem
provar a causa histórica do runner arm64.

## Correção

`continuity_observer.py` mantém um único transporte `docker exec --interactive` com um shell de sondagem no
container descartável. O shell não recebe SQL, senha ou comandos variáveis; executa somente `pg_isready` contra
127.0.0.1 como `duora_app`. Um desafio aleatório por pedido vincula a resposta ao probe atual, impedindo reutilizar
uma resposta antiga. O canal limita frames, aceita apenas o formato/códigos conhecidos e trata EOF, truncamento,
resposta alheia, timeout e encerramento inesperado como falha.

O prazo por probe permanece **2 s**, inclusive com verificação após a leitura para recusar uma resposta tardia
quando o host é desagendado. A admissão do transporte tem prazo de 5 s, correspondente à espera inicial do lab;
a primeira amostra continua sujeita ao limite original de **3 s** desde o início da janela. O sampler solicita
nova observação após 0,25 s e só timestampa o resultado depois de recebê-lo. Não há retry silencioso de probes.

`validate_observations` permanece sem alteração: erro de observador, fluxo de eventos morto, menos de duas
amostras, readiness negativa, lacunas de cobertura e a cobertura inicial/final continuam reprovando a promoção.
As verificações de sessão SQL persistente, IDs/timestamps/restarts/montagens/volume, banco/backup, guardas reais
do instalador e rollback compatível também permanecem. A indisponibilidade planejada da aplicação continua sendo
medida separadamente da continuidade do banco.

O processo é encerrado e recolhido tanto no sucesso quanto na falha. Problemas de encerramento não viram prova
positiva. O laboratório grava métricas sanitizadas de admissão/probe e categoria de erro; não emite o conteúdo do
canal, argumentos ou credenciais. O helper é somente do laboratório e não entra no inventário fechado do bundle
do executor de produção. Não há mudança de workflow, permissão, dependência, contrato HTTP ou serviço instalado.

## Regressões e qualificação

- Testes com processos/pipes reais: transporte lento, deadline após notificação de leitura, probe travado,
  readiness negativa, nonce incorreto/repetido, frame truncado/excessivo/inválido, morte e limpeza dos processos,
  prazo de admissão e falha de encerramento. O peer portátil testa o protocolo, sem simular SQL/locks do PostgreSQL.
- Testes de janela: cobertura inicial/final, lacuna maior que 3 s, limite exato de 3 s, readiness negativa e fluxo
  de eventos morto. Uma admissão de 5 s não permite primeira amostra depois do limite de 3 s.
- Qualificação nativa em `test_db_privileges.py`: atraso de CPU controlado de 2,1 s **antes** do exec reproduz o
  timeout do modelo antigo; o canal novo observa o PostgreSQL real dentro dos mesmos limites de cobertura e
  registra métricas por arquitetura. Uma parada real do container deve gerar erro ou nova amostra negativa;
  a asserção direta impede que uma parada lenta passe apenas por lacuna de tempo.
- O laboratório completo continua executando duas transições, rollback, sessão/sentinelas, identidade e os
  negativos de restart/backup. Os jobs `images (amd64)` e `images (arm64)` existentes executam a qualificação real;
  seus logs e artefatos do SHA candidato são a evidência de aprovação. Os testes portáteis não substituem isso.

Comandos de verificação:

```sh
python3 -m unittest discover -s scripts/deploy -p 'test_continuity*.py' -v
python3 -m unittest discover -s scripts/deploy -v
pyright -p scripts/deploy/pyrightconfig.json scripts/deploy scripts/backup infra/ansible/roles/docker/files
```

A qualificação real usa `DUORA_POSTGRES_IMAGE` somente no ambiente descartável autorizado dos jobs nativos,
conforme a configuração já existente. Não fabricar o ambiente de Actions nem apontar o laboratório para produção.
Os resultados finais e links das execuções pertencem ao PR da #326. Nenhuma intervenção em produção ou nova
reexecução administrativa é autorizada por este relatório.
