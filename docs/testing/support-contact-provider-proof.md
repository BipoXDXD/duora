# Prova dos provedores de SupportContact

Ferramenta de leitura para o operador responsável, não um endpoint ou alteração do runtime. A CI executa
somente doze casos sintéticos de JSON, ambiente, CLI e KeyPerFile, usando os provedores reais do .NET e o
`IOptions<SupportContactOptions>` registrado pelo `AddInfrastructure` real. Nenhum host é iniciado, nenhum
serviço de banco é resolvido e nenhum endereço externo recebe e-mail.

O helper recebe captura privada por stdin e carrega as bibliotecas do diretório de aplicação informado,
usando seu `.deps.json`. O artefato entregue contém somente a assembly do helper e seu runtimeconfig,
sem cópias das bibliotecas da aplicação. A referência de compilação apenas garante compatibilidade da API;
a execução resolve a Infrastructure da imagem observada. A CI verifica essa entrega sem bibliotecas locais
copiadas, com a aplicação Web sintética como componente nos casos Web e Worker.

O programa mantém stdout/stderr de bibliotecas silenciados. Saída: correspondência com o contato público
aprovado, origem do valor, MVID da Infrastructure e status enum; nunca valor divergente, argv, env, caminhos,
corpos de mensagem ou exceções. Contexto não suportado, arquivo obrigatório ausente ou options inválidas
produzem UNKNOWN; não se fabrica uma divergência conclusiva.

## Pedido focado ao operador 69, após os releases prioritários

Não executar antes da CI e revisão deste fonte/artefato e da passagem de uma janela compatível. Nenhum SSH
é realizado pelo autor desta frente. A única coleta solicitada deve usar o inventário/captura que o operador
já possui, limitada a Web/Worker: vincular container → imagem/recibo/source; obter contexto/args/env e arquivos
aplicáveis em memória privada; tornar `/app` e o SecretsDirectory reais legíveis apenas em montagem de
leitura para o helper, com os caminhos do contexto preservados. O helper requer `Microsoft.AspNetCore.App` inclusive para avaliar o Worker: usar runtime ASP.NET
isolado da versão compatível; a imagem Worker de .NET puro não basta. Não executar este helper dentro
do processo/container Worker em uso. Não herdar o env real no lançamento do CLR:
transmiti-lo somente no stdin privado depois que o helper já iniciou. Não instalar nada no container em
execução, criar endpoint, abrir rede de banco/provedor, alterar arquivo/configuração ou consultar participantes.
Se um helper descartável isolado exigir recurso/janela adicional, deixar a execução para o operador organizar;
esta preparação não autoriza instalação, Docker local, nova coleta geral ou interferência em publicação.

O perfil pressupõe entry assembly/contexto padrão `Duora.Web` ou `Duora.Worker`, Production, sem hosting
startup customizado. A captura deve identificar qualquer override de ApplicationName/ContentRoot ou provider
extra antes do replay; fora desse perfil o operador conserva UNKNOWN, sem usar o resultado como prova efetiva.
Todos os caminhos na captura devem referir-se à mesma preimagem somente leitura; não copiar settings reais
para o repositório, artefato de CI, chat ou estação do autor.

O resultado é **replay da configuração e do binding**, não leitura do cache do processo em produção:
`cached_runtime_options_observed=false`. Para provar valor efetivo desde o startup, o operador precisa
confrontá-lo com a mesma captura de identidade, providers/mounts e imutabilidade/timestamps dos arquivos
aplicáveis desde o início do processo. Um replay sozinho não fecha a #82. Emitir apenas boolean/origem e
limite correspondente no relatório sanitizado; não publicar os inputs ou o stdout bruto de outras ferramentas.
