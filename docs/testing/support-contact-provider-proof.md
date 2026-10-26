# Prova dos provedores de SupportContact

Ferramenta de leitura para o operador responsável, não um endpoint ou alteração do runtime. A CI executa
doze casos sintéticos de JSON, ambiente, CLI e KeyPerFile, usando os provedores reais do .NET e o
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

## Invocador privado do artefato preservado

run_preserved_probe.py é o invocador operacional, separado do driver sintético verify_replay.py.
Ele fixa os hashes dos três arquivos do artefato12589441146, gerado no head250
4da134f32f3cc0a2c699f776365f647549858f1a/run40168683154. O original permanece preservado.
Um helper recém-compilado por outro head não é aceito automaticamente: uma troca de pins exige
revisão explícita da fonte/artefato, sem opçãoCLI de bypass.

Verificação sem execução:

    env -i /usr/bin/python3 tools/support-contact-probe/run_preserved_probe.py --artifact-dir <dist-preservado> --verify-only

Esse modo exige exatamente os três arquivos, confere SHA-256, recusa sidecars, symlinks/arquivos excessivos e não lê stdin nem
inicia processo. artifact_ready significa somente integridade local. Não verifica runtime ou host.

Para execução futura pelo69 apósH3/230 e janela operacional, informar --dotnet com o caminho absoluto
do runtime ASP.NET10 já qualificado e fornecer a captura privada somente por stdin atéEOF.
Não colocar conteúdo da captura em argumentos, ambiente inicial, arquivo ou logs.
O invocador usa ambiente mínimo fixo, sem herdar credenciais; a própria captura só é aplicada
pelo helper depois do início doCLR. Captura máxima256KiB, stdout+stderr combinados16KiB e processo15s.
Processos descendentes da sessão criada são encerrados também quando o líder já saiu.
Diagnóstico emstderr, falha, timeout ou saída inesperada produz UNKNOWN sem texto bruto.

O perfil de entrada usa exatamente Role, ApplicationAssembly, ContentRoot, Environment e Arguments,
com os tipos do contrato existente. Roleweb/worker, caminhos absolutos, chaves únicas e nenhum
bootstrap hook. O helper continua responsável pelos providers reais e pelo perfilProduction.
O executor éPOSIX; a instalação real éLinux pelo69. Montagens readonly de artefato/app/config,
runtime e usuário sem privilégio qualificados, rede desabilitada e estabilidade das fontes são
pré-requisitos externos: o script não os cria nem finge observá-los. Hash antes do lançamento
não substitui staging imutável sob custódia do operador.

Saída é somente JSON com estados/enums/booleans/MVID e o head público do artefato. Campos extras,
correspondências não booleanas, providerother ou uma alegação de cache observado são recusados.
Exit0 significa integridade/replay mecânico; exit2UNKNOWN; exit3divergência do replay.
cached_runtime_options_observed, runtime_effective_proven e application_readiness_observed
permanecemfalse mesmo com sucesso. Não concluir82/SMTP/cache/saúde por exit0.

A CI acrescenta15regressões stdlib com bytes e processos Python sintéticos sob ambiente vazio,
sem imprimir kwargs/env/headers. Não executam.NET/Docker/contas reais.
O artefato separado support-contact-private-runner contém somente esse script e SHA256SUMS;
não confundir com o helper.NET e seu runtimeconfig. A instalação/execução real e a associação
de identidade/mounts/estabilidade continuam somente com69, sem nova rota pública ou flag.
