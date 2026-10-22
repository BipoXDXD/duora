# Publicação candidata do sanitizador

Complementa o [contrato S4](../api/media-sanitizer-release.md), sem alterar o
transporte Coolify das cinco imagens ou instalar componentes na aplicação.
O workflow `Publish media sanitizer candidate` só publica por dispatch da main,
com SHA completo idêntico à main atual e ao código do próprio workflow. Um PR
executa somente testes locais do contrato, sem licença nem permissão de publicação.

## Gates e produto da execução

Antes de construir/publicar, o produtor verifica a CI push da main do mesmo
repositório/SHA, última execução/tentativa, exatamente nove jobs e todos
`completed/success`. Jobs ausentes, duplicados, extras, ignorados ou pendentes
não qualificam. A identidade e a main são relidas; mudança de tentativa/SHA
recusa a publicação. As consultas não constituem transação atômica com GitHub:
uma mudança posterior à última leitura não é eliminada por esse gate.

Cada runner nativo amd64/arm64:

1. Constrói uma única imagem `sanitizer-release`, com bases fixadas e restore
   locked. A licença entra exclusivamente por secret mount obrigatório do
   BuildKit. O verificador existente lê histórico e todas as camadas, sem
   imprimir o segredo; o build falha se faltar licença ou houver vazamento.
2. Executa Trivy com a política vigente e gera CycloneDX dessa imagem. Publica
   o mesmo objeto, sem rebuild, com tag candidata exclusiva por SHA/run/attempt/arquitetura.
3. Pré-carrega seu digest imutável no daemon rootless dedicado do runner.
   A conta dedicada cria a pasta de autenticação com modo 0700 em seu próprio
   `/run/user/<uid>`; não depende de atravessar a área privada da conta do runner.
   Credenciais temporárias de registro são removidas antes do launcher; o
   decoder não recebe ambiente, configuração Docker, rede, storage ou segredo.
4. Executa os 14 casos reais S2. As falhas provocadas usam o probe separado
   registrado por config ID; a imagem Release exata executa o sanitizador e o
   template systemd restrito. O relatório distingue ambos, registra o config ID
   Release, sua referência imutável e o pin do perfil efetivamente ensaiado no
   serviço. Não afirma executar modos artificiais de falha dentro do decoder.
   No serviço restrito e com esse mesmo pin, a instrumentação de testes chama
   o adaptador real do Worker para os18vetores sintéticos S0. Compara resultado,
   rejeição, formato, dimensões orientadas, três variantes ordenadas, tamanhos e
   hashes com dois processos baseline frescos; identifica os WebP para conferir
   dimensões e ausência de EXIF/XMP/IPTC/ICC/CICP. A geração e a identificação
   são instrumentação no runner, fora do processo Worker de produção; o decode
   candidato usa a imagem Release com os limites existentes. Uma diferença,
   configuração parcial ou relatório incompleto faz a qualificação falhar.
5. Gera o pacote e valida seu manifesto/pin com o verificador S4. O artefato
   nativo só é enviado após sucesso; relatórios diagnósticos de falha não o substituem.

O agregador exige os dois artefatos da própria execução/tentativa, todos os
hashes e provas aprovados, mesmo commit/CI/run/attempt e imagens do repositório
permitido. Consulta o registro para conferir config ID e plataforma nativa,
simula o índice e verifica que ele mantém exatamente os dois digests ensaiados.
Confere os gates novamente antes da escrita. Somente então publica o índice
`candidate-<sha>-<run>-<attempt>` e seu digest, sem tag de produção/`latest`.

`media-sanitizer-candidate-<attempt>` contém `release.json` e os dois pacotes
por arquitetura. O relatório fornece imagem do índice, digests nativos,
manifest pins, profile pins e referências de CI/publicação. Os artefatos têm
retenção de 90 dias; evidência diagnóstica, um dia. São candidatos de CI,
não um canal permanente de distribuição de produção. O pin precisa ser obtido
desse workflow confiável, não de um pacote sem proveniência. Candidato nativo
publicado antes de uma falha posterior pode permanecer no registro; não recebe
um relatório final aprovado nem autorização de instalação.

## Execução e reversão

Após merge e CI verde da main, o operador autorizado dispara o workflow com o
SHA completo atual. Não se fornece licença/token como input. Selecionar branch
diferente ou um commit anterior falha fechado. A execução usa apenas runners
descartáveis; nenhum comando deste fluxo deve rodar no Mac ou no host da aplicação.
Não cancelar/reexecutar publicações para contornar falhas. Investigar a causa,
corrigir código em PR próprio quando necessário e preservar os gates.

O perfil ensaiado contém UID/GID/sockets/paths do runner e não serve como perfil
universal. Para instalação/homologação, ainda faltam host isolado selecionado,
janela e orçamento autorizados, pin do perfil daquele host e ensaio da release
selecionada com dados sintéticos, recursos efetivos e backup concorrente (#86).
O artefato adicional `media-native-corpus-<arquitetura>-<attempt>` preserva os18
resultados sintéticos por90dias, no mesmo workflow/attempt. Ele não amplia o
manifesto fechado de oito arquivos nem substitui a proveniência e os gates do
pacote. O processo de instrumentação tem prazo externo de20minutos e limpeza
de seu grupo; isso não amplia o prazo por foto ou o orçamento do sandbox.
Também permanecem limpeza durável do original, segurança/recuperação operacional
e aceite dos candidatos P174-3/4. A #174 fica aberta enquanto houver qualquer
critério pendente. Publicar não autoriza instalação, processamento de dados
reais, ativação de upload, serviços ou filtro regional.

Rollback só pode escolher conjunto anterior isolado, completo e compatível,
com imagem pré-carregada e perfil/pin correspondentes. Sem conjunto seguro,
parar consumo e manter upload desligado; nunca recolocar decode no Worker.
Este PR prepara publicação candidata e não executa rollback operacional.

Após a prova nativa, `media-host-instrumentation-<arquitetura>-<attempt>` preserva
por90dias os dois diretórios compilados da instrumentação sintética e
`global.json`, com a estrutura necessária para localizar o baseline. A compilação
ocorre antes da etapa que recebe a licença; o artefato não inclui diretórios de
autenticação, licença, configuração de host ou o workspace inteiro. Isso permite
repetir o corpus no host autorizado sem compilar no Mac ou na VPS.
Selecionar somente artefatos da execução/tentativa/commit qualificados e da
arquitetura correta, conferir integridade e caminhos antes de extrair e executar
como cliente sem acesso Docker, em grupo de recursos limitado. A instrumentação
contém o decoder de baseline apenas para comparação sintética; não é imagem de
produto, substituto do Worker ou parte do pacote fechado de oito arquivos.
Sua presença não comprova ensaio, capacidade ou backup e não autoriza instalação.
O artefato separado `media-host-probe-<arquitetura>-<attempt>` contém a imagem de
falhas já construída, escaneada e exercitada na mesma prova nativa, para não
reconstruí-la na VPS. Antes de qualquer pré-carga, conferir sua arquitetura e
configID contra `images.isolation-probe` da evidência qualificada; carregar
somente no runtime dedicado. É um probe adversarial de testes, não o sanitizador
de produto. Não executar o harness de runner descartável no host de aplicação:
os ensaios operacionais precisam restringir instalações, sinais e limpeza aos
recursos exclusivos e preservar os serviços existentes.

Referências técnicas: [BuildKit secrets](https://docs.docker.com/build/building/secrets/),
[imagetools inspect](https://docs.docker.com/reference/cli/docker/buildx/imagetools/inspect/)
e [imagetools create](https://docs.docker.com/reference/cli/docker/buildx/imagetools/create/).
