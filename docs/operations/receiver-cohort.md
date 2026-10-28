# Transição estática da coorte do receptor

Esta é uma operação administrativa separada da publicação de produto. A execução exige uma passagem
exclusiva própria, revisão independente do código/artefato e dos resultados do preflight. Este documento
não libera uma janela. O operador designado é o único escritor; um merge ou um CI verde não substitui a
passagem. Não cancelar, desativar, reexecutar ou despachar publicações para impor exclusividade.

## Conjunto qualificado

O receptor antigo é exclusivamente `1ccd1ada4777c80081c4f9bb2a64a500b1e221f794406319504d9e66cbb975cb`,
versão de instalação2.0.2. O candidato é exclusivamente
`e9af03f08162ecf2bda094d38511b44b810043863d3104f60ea1046ae687f3b3`, versão2.1.1. Ambos usam envelope2.
O emissor continua calculando o digest dos bytes reais do módulo; não há override de digest no ambiente.
`receiver_binding()` ordinário continua aceitando apenas o candidato e9. O perfil antigo privado existe
somente para a transação explícita ou para o journal local autenticado de uma tentativa interrompida.

O bundle continua contendo os quatro arquivos já previstos. A transição não altera os bytes do executor,
de `reconcile_payload.py` ou de `runtime_guard.py`: o manifesto do bundle antigo e o novo devem concordar
nesses três componentes. Somente o instalador muda. O bundle novo precisa conter o próprio instalador
invocado, com seu hash exato e a qualificação remota vinculada à fonte e ao artefato reais. Não preencher
hashes com os de um bundle histórico.

## Preflight e aplicação

A CLI existente `install_executor.py` recebe os parâmetros obrigatórios `--bundle`, `--source-commit`,
`--manifest-sha256` e `--artifact-id`. O acréscimo `--cohort-receiver` seleciona o arquivo candidato e9,
regular, protegido e root-owned. Sem `--apply`, a operação é um preflight de leitura: adquire os três
locks, verifica filas/ingressos, a coorte antiga, recibo/baseline, origem e inventário; não stageia versões,
altera ponteiros, escreve baseline ou cria journal/fence/receipt.
Sem um digest prévio, o primeiro inventário é produzido dentro dessa aquisição, depois da autenticação
das duas entradas e da verificação de ociosidade. A inspeção usa o módulo candidato do bundle validado;
o nome do diretório de uma entrada ativa não autoriza importar ou executar seu conteúdo.

Revisar o resultado completo, especialmente `inventorySha256`, `executorSha256`, `receiverBefore`,
`receiverAfter` e `cohortSha256`. A aplicação usa os mesmos fonte/bundle/artefato/candidato e acrescenta
`--apply`, `--reviewed-inventory-sha256`, `--expected-executor-sha256` e `--reviewed-cohort-sha256` com os
valores reais revistos. Mudança de contexto ou de qualquer preimagem provoca recusa; não substituir
automaticamente o valor esperado pelo novo valor observado.

Uma única aquisição segue a ordem host.lock → duora/release.lock → .deploy.lock. A mesma tentativa do
instalador contém ambas as preimagens no `attempt.json`, autenticado pelo SHA em `pending.json`.
`receiverCohort` descreve o par anterior e o destino literal. O fence durável existente
`duora-executor-install/pending.json` antecede qualquer troca. O registro `cohort-phase.json` acompanha
as etapas, mas a recuperação verifica o estado real e o journal; uma fase não autoriza sobrescrever
arquivos estranhos. Não há segunda chamada à CLI nem flag de locks implicitamente confiáveis.

O receptor e o executor são trocados sob os mesmos locks e fence. Duas renomeações não são uma operação
conjuntamente atômica. Os dois receptores recusam novos ingressos enquanto o fence existir. Só depois de
verificar o par final, inventário e preservação, o recibo é confirmado; pending privado é removido antes
do fence, que é liberado por último. Os diretórios versionados anteriores permanecem disponíveis.

## Interrupção e retorno

Conservar a tentativa original, sua identificação e seu SHA; não reaplicar para gerar outro prefixo.
A recuperação usa as opções existentes `--recover-attempt` e `--recover-attempt-sha256`, separadamente
de `--apply` e de um novo candidato. O bundle/fonte devem corresponder exatamente ao journal. A rota
autentica primeiro o fence e o journal locais, sem exigir que o artefato remoto ainda exista. A coorte
exige também o inventário de leitura atual e as filas ociosas. Indisponibilidade ou estado desconhecido
provoca parada, mantendo o fence; não há autorização de retorno cego ou bypass de fila.

Antes da primeira escrita, validar ambas as entradas, ambas as preimagens, os próprios temporários,
recibos/baseline, fonte e contexto de publicação. Mesmo hash de bytes em outro caminho não é identidade
aceitável. O retorno restaura somente os arquivos/ponteiros do próprio journal sob os mesmos locks e é
retomável com a mesma tentativa após interrupção. Os links ativo e salvo do executor exigem root:root,
tipo symlink, modo0777, link único e ancestrais protegidos, além do alvo literal e seus bytes qualificados.
Alteração em current, previous, intenção alheia,
target ou arquivo preservado bloqueia o retorno. Não apagar o fence como remediação manual.

## Preservação e provas

Não há deploy, restart, recreate, migração/replay SQL, backup/restore ou envio de email nesta transação.
Os arquivos da aplicação, assets, volumes, configuração regional, Resend e controles históricos são
preservados. O commit da aplicação permanece distinto de `sourceCommit` do instalador; instalar uma
coorte não publica a aplicação correspondente nem autoriza ativar recursos.

As provas novas exercitam arquivos root-owned, flock, interrupções e retorno em diretórios temporários
descartáveis de Actions. O core desses casos é sintético; não apresentá-los como nova qualificação SQL49,
runtime C9 ou rollback de produção. O supervisor testa seu próprio timeout e limpeza de processos
descendentes que possui; o limite de90s do processo externo sozinho não é prova de limpeza em UNKNOWN.
Os testes e a qualificação final de fonte/base precisam estar verdes antes de integração. Depois da
instalação estática efetiva ainda são necessários aceite independente e passagem para a publicação
automática de produto. Nenhuma dessas provas promete indisponibilidade zero ou operação contínua24x7.

As regressões de revisão usam também o snapshot histórico55d2, somente como fixture de teste fora do
bundle de quatro arquivos. Os controles executam os casos sintéticos no código anterior e exigem a
recusa no código corrigido, sem publicar um instalador com falha conhecida. Isso não é um novo job RED
anterior à correção: a reprodução dirigida e a verificação da correção são pareadas no mesmo CI remoto.
