# Inputs inativos do ensaio #174

O formatador `scripts/media/native_host_command_current.py` produz somente JSON
com um perfil candidato, seu hash e um vetor de criação do cliente standalone.
Não chama Docker, processos ou rede e não instala, cria, inicia ou autoriza nada.
A saída mantém `execution_authorized=false`, `profile_admitted=false` e nenhuma
alegação de corpus/host aprovado. P174-3/4 permanecem candidatos de homologação.

## Fonte e dados selecionados

Este input seleciona deliberadamente o consumidor F3
`f3f8c38d4619cccaf32c0c66c03e7421e0ab5fff`, o kit MAIN `12649779602`
(CI40321926935/attempt1, ZIP SHA256
`0e6fa11c0f92f3d1ac2f08ff305fe85dfb926e320428e97d4661ea10c0178a89`,
manifesto `f57def57991c587c6d06fef16d0d84c485d338c5519eda97849b673757bbc908`)
e o carrier AMD leaf F3. O executável é `Duora.MediaCorpusTests.dll`; a classe
linkada continua `Duora.IntegrationTests.Media.SanitizerResourceEnvelopeTests`.
Nenhuma assembly IntegrationTests ou fixture de banco é usada pelo comando.

O decoder licenciado ba5, seu bundle de oito arquivos e suas provas mantêm
identidades próprias. A igualdade entre commit do consumidor, kit e decoder não
é requisito do componente. Para outro consumidor, é preciso revisar seu vínculo
com este input e sua preservação; não reetiquetar o kit nem reconstruir o decoder
por causa de uma mudança de commit da aplicação. Nenhum artefato é baixado ou
executado por este formatador.

As duas fixtures em `scripts/media/fixtures/native174/` têm propósitos distintos:
`host-bindings.inactive.json` contém fatos operacionais NULL e é recusada;
`producer-profile.reference.json` é exclusivamente o vetor histórico de hash do
perfil sintético de CI. Seus UIDs não são identidades do host atual.

## Contrato com o operador e a #86

`compose(host, nonce)` recebe a estrutura fechada de dados e referências a recibos
independentes. Recusa fatos ausentes, consumidor/máquina divergentes, montagens
fora do namespace próprio, exposição do journal por resultados RW e slice sem o
nonce do operador. O hash do perfil mantém a codificação existente do launcher.
As referências são hashes, não assinaturas ou prova de atualidade/admissão.

O operador responsável continua obrigado a resolver e revisar os recibos, máquina,
ledger, lease, permissões, dispositivo/cgroup, identidade dos daemons, framework e
mapeamento UID do carrier, controles efetivos, orçamento integral sem dupla
contagem e cleanup/supervisor independente. O gerador não é esse controlador e
não valida recibos por consultar um hash remoto.

A observação QG de doze segundos não admite dispositivo, ausência de atividade ou
capacidade. Seu filesystem 8:1 e IOstat 8:0 não podem ser equacionados sem o vínculo
próprio. O orçamento S1 também não admite native174/#86. Nenhum helper, UID ou
caminho da VPS aposentada deve ser retargetado para o host atual.

Permanecem pendentes admissão operacional native174, controlador atual, perfil do
cliente externo da #86 (target/dataset/capability/limites), fixture e comando de
backup, corpus completo de 18 casos, sobreposição útil e concluída com backup
FULL/DIFF/WAL/restic, observação independente, recuperação/rollback/restore e
aceite operacional. NULL/UNKNOWN implica STOP antes de create/start. Os tetos do
cliente e sandbox são candidatos; deadlines, protocolo e limites do upload não
são alterados. O diretório de controle exige inspeção/permissões reais para que
seu bind RO exponha somente o endpoint admitido; não montar a API runtime no cliente.

## Validação

Os oito testes puros em `scripts/deploy/test_media_host_command_current.py` são
descobertos pelo job `ops` existente, sem alterar workflow, criar job ou disparar
ensaio de host. Usam somente fixtures sintéticas e biblioteca padrão: nenhum
processo, acesso à rede, Docker, SDK ou dados da aplicação. Testam recusas, vetores
de comando, contenção, imutabilidade da entrada e hash histórico do perfil.
A aprovação da fonte depende do CI desse PR; não se declara que os testes rodaram
no Mac. CI do formatador não comprova homologação do decoder ou capacidade do host.
