# Perfil hermetico candidato do SupportContact (#82)

Estado: **NOT_BUILT_NOT_QUALIFIED_NOT_ADMITTED**. Esta entrega implementa apenas
empacotamento estatico e a interface publica para a qualificacao do delta. Nao
executa Docker, Python do bundle, .NET, captura operacional, configuracao real,
banco ou transporte de e-mail. INV-05/INV-12 e T-05/T-12 continuam aplicaveis;
nenhuma API do produto, permissao existente ou limite do runner foi alterado.

## Proveniencia e escolha

A busca nos fontes locais do receiver/executor encontrou dependencia do Python
do host, sem bundle CPython selado reutilizavel. O perfil verifica o artefato publico inteiro e seleciona um fechamento minimo,
sem recompilar ou selecionar bibliotecas do Mac:

- Projeto: [astral-sh/python-build-standalone](https://github.com/astral-sh/python-build-standalone/releases/tag/20261021).
- Release `20261021`, id `419936095`, asset `663545914`.
- `cpython-3.13.17+20261021-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz`,
  `35356674` bytes; SHA-256 publicado
  `f829adc9d888e76c8801b4799ed444188d35239fcae3a9808a99ec5cb0d8d92c`.
- Origem do digest: campo `digest` do asset no
  [registro oficial da release](https://api.github.com/repos/astral-sh/python-build-standalone/releases/419936095),
  lido pelo GitHubApp gerenciado. Nenhum binario foi baixado localmente.
- Base publica ja fixada em `infra/images/dotnet/Dockerfile`:
  `mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374`,
  selecionada explicitamente para `linux/amd64`. O digest da folha e da configuracao
  do perfil composto continuam desconhecidos ate o executor produzir o recibo.

O [formato upstream](https://github.com/astral-sh/python-build-standalone/blob/20261021/docs/distributions.rst)
conserva a instalacao em `python/`; o sabor install-only nao inclui `PYTHON.json`.
O arquivo original e seu inventario de hashes permanecem preservados como
proveniencia. O contexto agora inclui somente interpreter real, libpython,
fontes `.py` da stdlib necessaria e `LICENSE.txt` de CPython. Exclui site-packages,
pip/ensurepip, idlelib/tkinter/turtledemo, test/tests e arquivos nativos opcionais
como `_dbm`/`_tkinter`. A
[documentacao de runtime](https://github.com/astral-sh/python-build-standalone/blob/20261021/docs/running.rst)
descreve GNU libc e bibliotecas de sistema. A combinacao com Noble e uma
candidata: resolver toda dependencia ELF e importar os modulos realmente usados
no executor continua obrigatorio. O pin do arquivo nao prova compatibilidade,
licencas aprovadas, ausencia de vulnerabilidades ou qualificacao do perfil.
Auditoria/SBOM e gates de supply-chain continuam exigidos; nao ha excecao nova.

## Contrato offline

Em um executor isolado aprovado, com os artefatos publicos previamente obtidos
pelo canal existente, preparar um **novo** arquivo de contexto:

```sh
python3 -I -B tools/support-contact-runtime/profile.py prepare \
  --python-archive /public-inputs/cpython-3.13.17+20261021-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz \
  --artifact-dir /public-inputs/original-helper \
  --runner /public-inputs/run_preserved_probe.py \
  --output /public-output/support-contact-context.tar
```

Esses caminhos exemplificam entradas publicas explicitas; nao sao locais
autoritativos nem comandos de provisionamento. O programa nao baixa, compila,
instala, chama subprocessos ou executa o contexto. Ele exige exatamente os tres
arquivos do helper preservado e verifica seus hashes originais, inclusive
DLL `11419c7d93a94666d535b5b5cfa2205d2ac0d1f8d0c840297f3eaa2489b044c3`,
e runner `87e09073a60a5c2ab12804c25160fa8d67c8cb53a297a052b0bd4d3733ab8211`.
Os bytes originais sao copiados sem alteracao. Nao existe entrada para captura,
configuracao, variaveis de ambiente privadas ou copia de diretorio arbitrario.

O hash e o tamanho publicado sao verificados antes da extracao. O parser impõe
64 MiB compactados, 512 MiB de arquivos descompactados e no maximo 30000 membros,
com teto adicional para cabecalhos/metadados descompactados. Rejeita paths
absolutos, traversal, duplicatas, conflitos arquivo/pai, bits especiais,
hardlinks, dispositivos, FIFO e symlinks fora de `reviewed-python-aliases.json`. Esse manifesto possui os 1048
aliases EXATOS inspecionados no arquivo oficial pinado e tem seu SHA-256 e asset
vinculados em `profile-lock.json`. O prepare exige igualdade da lista completa,
sem aceitar aliases adicionais ou omitidos. Alias de terminfo com `../` e
normalizado somente dentro de `python/`; cadeia tem teto de 64 passos e recusa
ciclos/escape. Destino final precisa ser arquivo regular do mesmo bundle. Todos
sao materializados como arquivos regulares; nenhum link do arquivo e instalado
no filesystem. Layout novo ou alias
nao revisado causa recusa e exige revisao explicita, sem fallback permissivo.

O contexto ordenado normaliza uid/gid/mtime para zero e permissoes para 0644 ou
0755, preservando executabilidade declarada. Inclui o fechamento minimo, helper,
runner, Dockerfile e registros publicos de proveniencia. `python-closure.json`
lista tamanho, modo e hash de cada arquivo, incluindo a origem dos aliases;
`construction-manifest.json` vincula seu hash ao lock, fonte do empacotador,
Dockerfile, base e bytes originais. Nao ha hashes de inputs privados. `python-original-inventory.json` conserva o inventario integral e
`python-selection.json` lista hashes, tamanhos e razoes de cada inclusao/exclusao.
A base e o fechamento minimo sao a declaracao candidata; o fechamento ELF real
e seus bindings so sao atestados no executor. O arquivo de saida existente nao
pode ser sobrescrito. A falha de validacao nao deixa extracao parcial.

O Dockerfile faz apenas `FROM`, `COPY` e metadados: sem `RUN`, apt, pip, SDK ou
recompilacao. A imagem publica base contem ASP.NET, sem grafo Duora. Os caminhos
reais candidatos sao `/opt/python/bin/python3.13` e
`/usr/share/dotnet/dotnet`; o runner usa esses caminhos diretamente, sem shim.
O ponto de entrada fixo e Python `-I -B`, runner original e helper preservado,
executando como `1654:1654`. A raiz read-only e o isolamento sao controles do
caller/executor, nao propriedades que um Dockerfile possa provar.

Para obter o contrato publico de qualificacao sem executar nada:

```sh
python3 -I -B tools/support-contact-runtime/profile.py qualification-plan \
  --image sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
```

O digest acima e apenas um exemplo de sintaxe; substituir pela folha composta
real. A resposta permanece `NOT_BUILT_NOT_QUALIFIED_NOT_ADMITTED` e
`operational_capture_authorized=false`. O teto proposto e 512 MiB de memoria,
swap total igual a memoria (sem swap adicional), 0.5 CPU, 64 PIDs, tmpfs privado
16 MiB e core zero, com UID/GID1654, rede `none`, raiz RO, cap-drop ALL e
no-new-privileges. Esses valores nao sao medicoes ou limites ja admitidos.
O caller deve confirmar efetivamente todos os controles, nenhuma exposicao de
socket/proc/PID do host e montagens RO somente das folhas fixas autorizadas da
mesma preimagem sintetica, em `/app` e `/run/secrets`.

## Testes novos e qualificacao restante

TDD local: oito testes novos falharam primeiro por ausencia do empacotador;
a primeira implementacao passou. Um novo teste de modo executavel de biblioteca
falhou com 0644 em lugar de 0755; o ajuste preservou o modo normalizado e a suite
voltou a passar. O teste inicial que so detectava a existencia do arquivo foi
removido; permanecem oito testes de comportamento de empacotamento.

Comando leve usado no Mac (apenas stdlib e fixtures publicas sinteticas):

```sh
env -i PATH=/usr/bin:/bin /usr/bin/python3 -B -m unittest discover \
  -s tools/support-contact-runtime -p test_profile.py -v
```

A preparacao offline com o bundle oficial real foi executada uma vez apos a
correcao de compatibilidade dos aliases, com resultado exit0. A inspecao previa
do arquivo comprovou 4528 membros, 99189121 bytes de arquivos e 1048 symlinks,
zero hardlinks e 45 arquivos de licenca. O contexto resultante materializou os
1048 aliases como arquivos regulares, sem links filesystem. Esse primeiro fechamento integral foi substituido pela selecao minima apos a
identificacao de dependencias opcionais fora da politica de licencas. O novo
prepare offline tambem terminou exit0, com 539 arquivos incluidos,
3989 excluidos e SHA-256 de fechamento
`d7749183848fca298b7404d5849d0c166733192c04ddde7975732fe303b1b617`.
Isso prova somente
preparacao/extracao; construcao/execucao da imagem e toda qualificacao remota
permanecem pendentes. Nao foram repetidos os 15 casos do runner, 12 casos
de IOptions, CI anterior, full-image outbox, C934, SQL48, HTTPS, SMTP ou provedores.
Os gates normais exigidos para o novo head continuam obrigatorios no ambiente
aprovado, sem alterar ou reduzir workflows.

Depois da inspecao real, o teste novo de alias com pai relativo/cadeia reproduziu
RED (`archive_link_target_invalid`), seguido de GREEN10 apos o manifesto exato e
resolucao limitada. A recusa original do arquivo real foi
`archive_link_or_type_invalid`; a preparacao posterior passou sem mudar bytes do
helper/runner ou afrouxar a correspondencia exata do manifesto.

No executor remoto isolado existente escolhido pela coordenacao, qualificar
somente as novas fronteiras: hashes do fechamento
e folhas OCI/config; binarios ELF reais Python/ASP.NET e suas dependencias;
controles efetivos e limites; fornecedor/caller com autoridade fixa, selecao de
identidade e recusa anterior a leitura privada; sentinelas sinteticas apenas em
RAM, EOF e prazo total; uma parelha mecanica sintetica usando o helper original;
schema fechado, ausencia de vazamentos e limpeza integral do sandbox possuido.
Nao usar inputs, servicos ou pessoas reais para essa qualificacao.

O recibo novo deve vincular lock/contexto/fechamento/folha/config, fonte do
fornecedor/caller, revisao independente e pins publicos
`9c779cebc3dc3af2a0b0c0c945fef86460210bd19bcd6187f68e0a6e536bf128`.
Qualificacao positiva do perfil permite avaliar a passagem exclusiva da #69;
nao executa captura operacional, nao observa cache e nao fecha #68/#82/#93.

## Correcao de licencas: selecao minima

O inventario upstream revelou Berkeley DB em `_dbm` e dependencias vendorizadas
de pip fora da allowlist. Nao foi criada excecao. O teste novo reproduziu RED
para exclusao desses arquivos e para recusa de ELF disfarçado de fonte `.py`;
a implementacao passou os 12 testes de empacotamento.

A allowlist de arquivos de extensao nativa contem somente `_posixsubprocess`,
`select`, `_struct`, `_json`, `_sha1`, `_sha2`, `_sha3`, `_md5`, `_blake2`,
`_random`, `_bisect`, `math`, `binascii`, `unicodedata`. O arquivo oficial possui
apenas `_dbm` e `_tkinter` como extensoes dinamicas externas; as demais extensoes
sao compiladas no interpreter. Selecao de arquivos **nao remove codigo embutido**
de `_ssl`, `_hashlib` ou outras extensoes compiladas. Nenhum tal resultado e
alegado. `native_dependency_license_review=PENDING` permanece no manifesto.

Inspecao estatica dos headers ELF, sem executar binarios, encontrou somente
libc/libm/libpthread/libdl/libutil/librt e o loader GNU em `DT_NEEDED` dos dois
arquivos centrais. Hashes e listas observadas estao no lock. Essa evidencia
exclui dependencia dinamica desses arquivos centrais em Berkeley DB, mas nao
e uma auditoria completa das dependencias estaticas ou de seus avisos. A revisao
dos notices e licencas do codigo compilado continua necessaria antes de admitir
o perfil. O Dockerfile agora fixa tambem `PATH=/usr/bin:/bin` explicitamente.

## Bloqueio obrigatorio antes de build/exportacao

A selecao minima nao resolve a licenca do codigo nativo compilado dentro do
interpreter. O lock registra `license_admission.status=NOT_ADMITTED` e nenhum
hash de recibo. `qualify.py` verifica esse gate ANTES de download, preparo,
build ou exportacao OCI e falha com `LICENSE_NOT_ADMITTED`; nao produz um perfil
redistribuido como efeito da qualificacao. O job deve ficar vermelho por esse
bloqueio material, sem skip ou sucesso sintetico.

Uma liberacao futura exige recibo independente, revisado e pinado por hash,
vinculado ao arquivo oficial, com inventario nativo estatico e notices completos
aprovados e zero excecoes de politica. Alternativamente, substituir por uma
construcao CPython previamente aprovada e com inventario/avisos verificaveis.
Nao ha aprovacao implicita neste candidato. Fonte pode seguir para PR draft;
merge, construcao e admissao permanecem bloqueados.

O teste novo do gate teve RED antes da implementacao, depois 6 testes puros de
qualificacao passaram. A selecao minima teve RED de exclusao/ELF disfarçado e
um caso adicional de inventario completo: aliases opcionais com diferencas de
maiuscula/minuscula podem colidir no filesystem do Mac, portanto inclusao e
exclusao usam o inventario original verificado e conferem bytes dos arquivos
retidos. Todos os 4528 nomes originais permanecem contabilizados: 539 incluidos,
3989 excluidos; 12 testes de empacotamento passaram. O prepare final so criou um
contexto offline; nenhum binario foi executado e nenhum OCI foi construido.

## Notices verbatim no contexto

A ausencia dos textos conhecidos e uma correcao de empacotamento: o prepare
agora verifica e copia exatamente os 18 arquivos declarados em
`python-license-inventory.json.supplemental_notice_files` para `notices/`, fora
do fechamento Python. Exige nomes planos no prefixo fixo, bytes e hashes exatos,
sem symlink, arquivo extra ou escape. O inventario e copiado sem alteracao;
`notice-manifest.json` vincula seu hash aos hashes e tamanhos de cada texto.
O manifesto de construcao vincula ambos e todos os notices. O Dockerfile copia
os textos para `/opt/probe/notices` e os registros para a proveniencia.

A preparacao offline real terminou exit0 e os 18 textos foram conferidos byte a
byte no contexto resultante. O fechamento Python permaneceu
`d7749183848fca298b7404d5849d0c166733192c04ddde7975732fe303b1b617`.
Isso comprova presenca no contexto, sem afirmar presenca numa imagem executada
ou aprovacao da composicao nativa. Os textos concedem direitos condicionais nos
seus proprios termos; uma lacuna de allowlist interna nao equivale a ausencia
de permissao legal. O inventario exato de versoes e componentes compilados e a
conformidade das condicoes aplicaveis continuam sem admissao; nao ha waiver.

O gate futuro de admissao reconstroi e confere o hash de `notice-manifest.json`
antes de download/build e compara novamente o hash produzido no contexto antes
de construir ou exportar. Mudanca de texto, inventario ou manifesto invalida o
recibo. Teste novo observou RED para ausencia/tamper de notice e para mudanca no
manifesto construido, seguido de 13 testes de empacotamento e 8 de fronteiras da
qualificacao verdes. Nenhuma prova antiga ou runtime foi repetido.
