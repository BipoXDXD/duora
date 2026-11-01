# Observador de continuidade do banco na CI

O laboratório descartável de `scripts/deploy/continuity_lab.py` qualifica dois deploys de aplicação,
recuperação e rollback sem interromper o PostgreSQL ou o backup. Executa nos runners nativos de imagens,
em CI e publicação; não é uma ferramenta de operação de produção. Atende à preservação dos gates da
spec 17.1 e ao isolamento de INV-12/ADR-0014; nenhuma API ou estado de participante muda.

## Incidente e limites da evidência

Na [release 41294826228, tentativa 1](https://github.com/BipoXDXD/duora/actions/runs/41294826228/attempts/1),
o [ARM64 124813429169](https://github.com/BipoXDXD/duora/actions/runs/41294826228/job/124813429169) falhou
em 01/11/2026 às 09:56:02 UTC com `database:TimeoutExpired`. O relatório registrou uma amostra, janela de
aproximadamente 49,13 segundos e fluxo de eventos ativo. O erro interrompeu o sampler; corretamente, a
janela sem cobertura não qualificou a publicação.

O código iniciava um novo `docker exec … pg_isready` a cada amostra, com deadline externo de dois segundos.
Esse orçamento incluía lançamento/attach do Docker, agendamento e execução da sondagem. Além disso,
[`pg_isready` espera três segundos por padrão](https://www.postgresql.org/docs/18/app-pg-isready.html):
o prazo externo podia vencer antes de obter seu resultado. Não havia diagnóstico de fase nem duração
por amostra. O log não permite atribuir o atraso original ao daemon, runtime, CPU ou resposta do banco;
não comprova indisponibilidade nem que o runner ARM64 estivesse saturado.

A [tentativa 2](https://github.com/BipoXDXD/duora/actions/runs/41294826228/attempts/2), no mesmo commit
`96cd4bdf3ba3eaabc9b45b5fe2c91453f04ff709`, passou: 149 amostras, maior intervalo de 0,334 segundo, nenhum
erro do observador. O PR #322 modificava somente documentos. O sucesso da repetição não identifica a
causa do atraso nem substitui a correção da fragilidade do observador.

## Correção proposta

`DatabaseObserver` abre um único `docker exec --interactive` para um shell de requisição/resposta,
com handshake limitado a cinco segundos **antes** da janela. Cada solicitação executa um novo
`pg_isready` no PostgreSQL real, por TCP loopback, com timeout explícito de um segundo. O cliente
continua exigindo resposta completa em dois segundos por amostra, sem retry, resposta em cache ou
conversão de timeout em sucesso. A cadência de 0,25 segundo entre sondagens é preservada.

Assim, a janela medida não depende de criar e anexar um novo exec Docker a cada sondagem. A inicialização
tem limite próprio; erro nessa fase impede abrir a janela. A janela continua começando antes da instalação
estática, dos dois deploys, da recuperação, do rollback e das guardas negativas de leitura.

O protocolo admite apenas o handshake e os códigos 0–3 do `pg_isready`. Saída parcial não pode bloquear
`readline`: leitura por selector e bytes usa um único deadline absoluto, com tamanho limitado e prazo
revalidado depois de `os.read`, inclusive quando o pipe já estava pronto antes de o host perder agendamento.
`sample()` também confere um checkpoint final após o helper/frame: captura timestamp e duração em locais,
rejeita duração maior ou igual a dois segundos e devolve esses mesmos valores, sem recalcular relógios no retorno. EOF,
protocolo inválido, atraso e processo morto são erros. Códigos 1, 2 e 3 continuam sendo amostras de banco
não pronto, que reprovam a qualificação. Diagnósticos contêm somente fase/motivo fixos; stdout/stderr do
filho não são incluídos em erros. O relatório acrescenta tempo de inicialização, duração e código de
cada sondagem, sem dados de participantes ou credenciais. O timestamp da amostra é obtido após receber e
validar a resposta, sem representar o início de uma requisição como observação já concluída.

Encerrar o observador fecha stdin; o shell sai por EOF, sem loop remoto autônomo. Uma prova positiva exige
transporte vivo até o fechamento e saída normal após EOF. Morte anterior, saída não zero ou necessidade de
matar o processo são falhas, nunca aprovação. Já a limpeza de uma prova que falhou usa `abort`, recolhe o
processo e preserva o erro original, inclusive no construtor, context manager e `finally` do laboratório.
O teardown continua removendo somente seus contêineres e volumes descartáveis.

Permanecem obrigatórios e sem relaxamento:

- prazo externo de dois segundos por sondagem e cobertura máxima de três segundos entre amostras,
  inclusive início/fim da janela;
- todas as amostras de banco prontas, pelo menos duas amostras, nenhum erro e fluxo de eventos vivo;
- ausência de eventos de ciclo de vida de PostgreSQL/backup durante a janela positiva;
- identidade dos contêineres, sessão SQL persistente, sentinel, histórico de migrations e ACLs;
- recuperação, rollback e casos negativos reais do laboratório;
- gates das duas arquiteturas, privilégios, restauração/PITR, scanners, SBOMs e validação de digests.

Nenhum workflow, timeout de job, exceção de scanner, regra de proteção, imagem PostgreSQL qualificada
ou executor de produção é alterado. O código afeta somente a coleta de evidência descartável da CI.

## Regressão e validação

`DatabaseObserverImageTests` executa automaticamente no passo existente de privilégios nas duas
arquiteturas, sem mudar o workflow. Cria um cluster PostgreSQL descartável, usuário 999, rede `none`,
TCP loopback e dados sintéticos, sem portas do host ou armazenamento persistente. Três cenários:

1. Injetar atraso de 2,2 segundos no lançamento de cada exec Docker: o algoritmo original excede os dois
   segundos apesar do banco saudável; o observador persistente abre o transporte antes da janela e
   coleta novas sondagens prontas com cobertura válida.
2. Pausar o PostgreSQL depois de uma sondagem pronta: a próxima solicitação continua falhando pelo
   deadline de dois segundos. Não reutiliza o resultado anterior.
3. Suspender somente o processo servidor, mantendo o shell e o transporte ativos: sondagens novas
   retornam código 2 (sem resposta) e a cobertura reprova, sem confundir isso com falha do transporte.

Os testes de processo/protocolo cobrem observador silencioso, resposta parcial, EOF, status não pronto,
saída malformada, inicialização sem resposta, resposta/greeting tardios apesar de readability, timestamp de
recebimento e falhas de fechamento sem mascarar o erro da janela. Os testes preexistentes de cobertura recusam perda de
eventos, erro do sampler e lacunas no começo, meio ou fim. O laboratório completo continua sendo prova
adicional obrigatória no runner hospedado, com imagens candidatas e histórico qualificado reais.

Comandos focados, após construir a imagem PostgreSQL derivada conforme `test_db_privileges.py`:

```sh
python3 -m unittest discover -s scripts/deploy -p 'test_*observ*.py' -v
DUORA_POSTGRES_IMAGE=duora-postgres:test PYTHONPATH=scripts/deploy \
  python3 -m unittest test_db_privileges.DatabaseObserverImageTests -v
python3 -m unittest discover -s scripts/deploy -v
pyright -p scripts/deploy/pyrightconfig.json scripts/deploy scripts/backup infra/ansible/roles/docker/files
```

## Risco e reversão

Um runner realmente sem capacidade de produzir novas sondagens dentro dos prazos continuará falhando;
esta mudança não promete eliminar toda intermitência de infraestrutura. Um novo erro informa se falhou
o transporte inicial ou uma sondagem, em vez de afirmar uma causa não demonstrada. Não aumentar limites
nem omitir amostras para obter aprovação. Reverter o PR restaura o observador anterior, sem migração de
dados ou ação em produção. A publicação de #322 já foi confirmada separadamente; esta proposta precisa
de revisão e dos checks próprios antes de integração.

## Revisão independente e reconciliação com o PR #330

A revisão do head inicial do #328 (`e535502…`) reproduziu uma lacuna: se o host perdesse agendamento após o
selector indicar dados prontos, a resposta podia ser aceita após o limite de dois segundos. Um ensaio com
processo isolado, pipes/relógios reais e SIGSTOP/SIGCONT qualificou duas sondagens de aproximadamente 2,21 s.
Os intervalos ainda eram menores que três segundos; por isso a cobertura não substituía a checagem do prazo.
Não se trata de comprovação da causa do incidente histórico.

O [PR #330](https://github.com/BipoXDXD/duora/pull/330) continha deltas úteis para esse problema. A consolidação
permanece no #328: prazo conferido depois da leitura, timestamp no recebimento e fechamento estrito, com
regressões escritas e observadas falhando no código anterior. Também é testada a preservação do erro primário
quando o cleanup precisa matar um peer travado. Os três cenários nativos do #328 continuam em clusters próprios;
não foi incorporado o stop/restart do fixture compartilhado de privilégios do #330, que interferiu em testes ACL.

O nonce por pedido do #330 não foi incorporado: não foi demonstrado replay no shell serial real. O shell
mantido executa uma sondagem nova por requisição e o leitor rejeita frames inválidos ou excedentes; não há retry
ou reaproveitamento de amostra após timeout. Nenhum workflow, limite ou gate é relaxado por esta consolidação.

A revisão final do head `881729a…` demonstrou outra pausa de agendamento possível, depois do retorno do helper
e antes do registro da amostra. Com pipes e relógios reais, SIGSTOP/SIGCONT produziu uma resposta saudável de
aproximadamente 2,11 s que a cobertura de três segundos ainda aceitava. Uma regressão nova reproduz esse ponto
depois de uma leitura realmente bem-sucedida. O checkpoint final de `sample()` rejeita essa resposta e congela
os dois valores verificados. A CI e as seis provas de `881729a…` são históricas e não aprovam o SHA corrigido;
é necessária CI completa nova. Os limites e a qualificação dos três trials permanecem inalterados.

## Três qualificações completas por arquitetura (#327)

O teste nativo existente `test_real_two_deploy_continuity_guards_installer_and_compatible_rollback` executa
agora três laboratórios completos em sequência em cada arquitetura. Cada chamada cria seu próprio diretório,
registro OCI descartável, containers, volumes e sessão SQL, executa todos os cenários e encerra seus recursos.
Qualquer exceção ou asserção interrompe a série: não há retry nem aceitação de duas execuções após uma falha.
Os três testes negativos de `DatabaseObserverImageTests` continuam separados e não contam como esses trials.

Os trials 1 e 2 não adicionam carga. No terceiro, `TrialCpuLoad` mantém um worker de CPU continuamente
executável, com `nice` acrescido de 10, sem alterar recursos ou configuração dos containers. O worker tem teto
fixo de 300 segundos e registra CPU efetivamente consumida, tempo de parede, lotes de cálculo e motivo de
término. O processo deve permanecer vivo até o fim das operações qualificadas e encerrar normalmente após
solicitação; expiração do teto, morte antecipada, métricas inválidas ou encerramento forçado reprovam o trial.
Não se infere uma pressão de CPU específica apenas da existência do worker: os valores medidos acompanham
o relatório, juntamente com número de CPUs e load averages antes/depois.

A carga começa antes do bloco de qualificação e seu intervalo deve conter integralmente esse bloco, incluindo
a janela de observação, instalação, deploys, recuperação, rollback e guardas negativas. As métricas são
validadas e o worker é recolhido **antes** de gravar artefato positivo. Cleanup em `finally` também remove o
worker em caso de erro. Esse perfil controlado delimita uma condição de contenção; não comprova que o runner
original sofria essa carga nem promete resistência a toda saturação.

Cada relatório inclui `qualificationTrial`: ordinal, intervalo, carga, número de amostras, maior duração de
sondagem, maior lacuna e cobertura das extremidades. O marcador `[continuity-trials]` só é emitido depois dos
três sucessos. O processo root salva os próprios artefatos, preservando o consumidor atual:

- trial 1: `<arquitetura>/qualification.json` e `<arquitetura>/bundle/`;
- trial 2: `<arquitetura>/trials/2/qualification.json` e seu `bundle/`;
- trial 3: `<arquitetura>/trials/3/qualification.json` e seu `bundle/`.

Artefato existente não é sobrescrito. O upload recursivo já existente da CI conserva a árvore, sem mudança
de workflow ou retenção. O mesmo teste continua nos jobs nativos de publicação. A execução repetida aumenta
o trabalho do teste; não aumenta timeout de job, prazo por sondagem ou lacuna permitida.

Os testes portáteis verificam preservação dos três relatórios/bundles, recusa de identificadores inválidos,
trabalho real do worker e rejeição de morte antecipada. **Não comprovam os seis laboratórios completos.**
O aceite da #327 depende dos resultados e artefatos reais do novo SHA em amd64 e arm64. Enquanto essa prova
não estiver aprovada e revisada, a issue permanece aberta; a CI verde do head anterior não a substitui.
