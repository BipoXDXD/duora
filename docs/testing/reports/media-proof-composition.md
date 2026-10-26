# Composição das provas de mídia — #174

Esta entrega reúne o harness standalone de #231, sua execução e instrumentação de
#232, a retenção de evidências e a regressão APT de #227. Não altera contratos HTTP,
o sanitizador, os limites de upload ou a ativação do pipeline. P174-3/4 do
[ADR-0036](../../architecture/adr/0036-isolamento-do-media-sanitizer.md) continuam
candidatos de homologação.

## Fronteiras preservadas

`Duora.MediaCorpusTests` reutiliza explicitamente as fontes sintéticas do corpus,
sem carregar o assembly de integração e sua fixture PostgreSQL. Seu teste de
fronteira rejeita registro de fixture de assembly e referência a Testcontainers ou
ao assembly de integração. As dependências continuam travadas pelo lockfile.

Nos runners nativos amd64 e arm64, o corpus standalone roda com entrada somente
leitura, UID não root, rede desabilitada, rootfs somente leitura, capabilities
removidas e sem socket Docker. Apenas os binários e `global.json` são copiados para
o kit, cuja árvore recusa links simbólicos. O orçamento desse cliente de testes é
distinto do orçamento candidato do sandbox; não é mudança dos limites do produto.

A regressão APT com metadados assinados, chave efêmera e servidor HTTP local roda
antes dos pré-requisitos reais. O controle negativo reproduz a fonte herdada 403;
fontes necessárias indisponíveis e assinatura inválida continuam fatais. Os mesmos
argumentos de update/install são extraídos dos workflows de isolamento e release.
Detalhes em [media-rootless-apt.md](media-rootless-apt.md).

O job licenciado continua restrito a dispatch confiável da main exata, após os nove
checks gerais. Ele usa o assembly standalone para comparar os 18 casos com a imagem
Release licenciada fixada por digest, validar as três variantes e verificar a
matriz de falhas. O manifesto agrega somente provas do mesmo commit, arquitetura,
run e attempt. Jobs licenciados omitidos em um PR não constituem publicação.

## Evidência e retenção

Os relatórios `media-isolation-*` e `media-release-proof-*` têm retenção solicitada
de **90 dias**, assim como os demais artefatos candidatos já configurados. O
relatório diagnóstico da release continua elegível depois de falha ou sucesso da
etapa de prova; não passa a ser prova qualificada apenas por ter sido preservado.
As permissões, conditions dos jobs, falhas obrigatórias e scanners são mantidos.

Cada resultado usado na #174 deve registrar produtor, commit de fonte, run,
attempt, arquitetura, digest do arquivo e, quando aplicável, digest OCI/config e
pin externo do manifesto. Digest do ZIP de Actions não substitui digest OCI ou
pin do bundle. Preservar arquivo antigo permite auditar seu resultado histórico;
não renova sua validade para outra composição. Retenção solicitada também não
garante disponibilidade depois de expiração ou exclusão administrativa.

## Validação da composição e homologação

O aceite de integração exige nove checks gerais e os dois jobs nativos da
composição final. Nos runners, verificar restore travado/build/testes do harness,
corpus sem API Docker/rede, cinco regressões APT e a matriz real de falhas, sem
skip de caso obrigatório. Não executar essas provas no Mac.

O resultado de PR não substitui o CI push/main pós-merge nem a publicação
licenciada do SHA escolhido. A publicação válida da instrumentação é pré-requisito
para o operador autorizado consumir o novo harness no host identificado e aprovado
para o ensaio; não copiar instrumentação expirada ou de outro commit por conveniência.
A VPS antiga foi aposentada como alvo. O host atual exige novo perfil, identidade,
recursos, pin e revisão próprios, além de passagem operacional explícita. Não
reativar nem redirecionar scripts antigos ou transferir seu aceite ao host atual.

A homologação #174 permanece condicionada à prova completa do corpus no host,
enforcement e orçamento integral de Worker/buffers/launcher/daemon/sandbox,
sobreposição real com backup, diagnóstico das interrupções PSI, observação
independente, recuperação/restore e aceite operacional com #86. As tentativas
interrompidas e as falhas de sondagem anteriores continuam evidência de limite do
ensaio, sem aprovação retroativa. Os resultados finais são anexados à issue com
suas identidades; este documento não antecipa sucesso de nenhuma execução.

## Reversão

Uma reversão mantém upload desligado e interrompe com segurança o uso de uma
instrumentação não qualificada. Não conceder Docker API ao cliente, retornar ao
decode local no Worker ou restaurar APT irrestrito para contornar falhas. A retirada
do harness precisa preservar os gates e retirar seu uso dos workflows de forma
coerente; relatórios históricos continuam preservados para auditoria.
