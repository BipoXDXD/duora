# Instrumentação standalone para a #174

A CI geral preserva os outputs que o job `backend` já compilou e testou, depois dos
testes obrigatórios. Não há outro restore, build, teste de decoder ou publicação
de imagem. O artefato `media-host-instrumentation-amd64-<attempt>` contém
`global.json`, os diretórios completos de saída Debug/net10.0 de
`Duora.MediaCorpusTests` e `Duora.MediaBaselineHost`, e `instrumentation.json`.

O manifesto identifica commit e árvore efetivamente presentes no checkout, run,
attempt, job, arquitetura do runner, caminhos, tamanhos e SHA256 de cada arquivo.
O pacote inclui deps/runtimeconfig e dependências; recusa arquivos especiais,
links simbólicos e `Duora.IntegrationTests.dll`. O assembly standalone compartilha
as fontes do corpus, sem importar a fixture PostgreSQL do assembly de integração.
O empacotador não executa nenhum binário ou script do pacote.

Para aceitar o pacote, obter o ID e digest externo do artefato pela API autenticada
do GitHub e conferir a execução CI/attempt/source exatos, o sucesso dos nove jobs
e todos os arquivos do manifesto. O manifesto interno sozinho não comprova a
origem. Em PR, o checkout e o artefato podem pertencer ao merge de teste; isso não
é um recibo de publicação na main. A arquitetura registrada descreve esse build,
sem provar execução em outra arquitetura ou num host real.

A fonte da instrumentação, a aplicação consumidora e o componente decoder são
identidades separadas. O componente licenciado histórico ba5/39872368339 pode ser
avaliado para reuso com seus próprios pins e relatórios. Não reescrever seu commit,
manifestos ou labels para o commit da aplicação. O contrato de oito arquivos do
decoder permanece intacto; o kit não substitui esse bundle nem sua licença.

A política versionada em `scripts/supply-chain/license-policy.json` registra
ImageSharp 4.1.3, a leitura do LICENSE e a declaração do responsável aplicável ao
projeto. Preservar essa condição e reavaliar se mudar; o SBOM ou o kit não concedem
direitos novos e nenhuma chave deve ser incluída no artefato.

A fonte publicada A0A e seus recibos são próprios. Uma comparação delimitada do
consumidor pode ligar interfaces iguais ao componente anterior, mas não transfere
digests de aplicação, SQL ou aceite de C934 para A0A.

O download e a conferência offline deste kit não autorizam instalação ou execução.
As provas no ATUAL dependem de perfil revisado, limites e orçamento integral,
enforcement/cleanup, corpus completo, carga com sobreposição útil ao backup em
coordenação com a #86, observação independente, recuperação/restore e aceite
operacional. A VPS antiga permanece aposentada. **P174-3/4 são candidatos; a #174
continua aberta e o upload não é ativado por esta entrega.**
