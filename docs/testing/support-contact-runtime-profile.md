# Perfil candidato SupportContact: empacotamento e admissao limitada

Estado das licencas: cinco casos **PROFILE_ONLY_APPROVED**, limitados aos
digests do recibo agregado pinado no lock. Estado do runtime: **NOT_QUALIFIED**.
O recibo positivo nao amplia a politica geral; uma mudanca em seus bindings
exige reconciliacao antes de download/build. Esta fonte prepara um perfil
sintetico da #82; nao autoriza captura operacional, cache, configuracao real,
merge ou deploy.
INV-05/INV-12 e T-05/T-12 continuam aplicaveis. Os fontes originais do helper e
runner permanecem inalterados; nenhuma API, permissao ou politica global muda.

## Entradas atuais e proveniencia

O arquivo oficial CPython install-only-stripped 3.13.17, release20261021,
asset663545914, possui35356674 bytes e SHA-256
`f829adc9d888e76c8801b4799ed444188d35239fcae3a9808a99ec5cb0d8d92c`.
O digest veio do registro oficial da release via GitHubApp gerenciado.
O arquivo inteiro e verificado antes da selecao. Nenhum runtime do Mac e copiado.

A selecao minima conserva interpreter, libpython, fontes `.py` da stdlib e
CPython LICENSE.txt; exclui site-packages/pip/ensurepip, idlelib/tkinter,
turtledemo, tests e arquivos nativos opcionais como `_dbm`/`_tkinter`.
539 arquivos sao incluidos e3989 excluidos. O fechamento Python ja observado e
`d7749183848fca298b7404d5849d0c166733192c04ddde7975732fe303b1b617`;
a selecao, incluindo seu diretorio de runtime obrigatorio, tem SHA
`d986bc6b784585d80896900be5ad3593cc10c27830df6fde4c2372a1bf21b466`.
A selecao de arquivos nao remove codigo nativo compilado dentro do interpreter.

O diretorio vazio `python/lib/python3.13/lib-dynload` permanece no contexto TAR,
com modo0755, uid/gid0 e mtime0. O [getpath do CPython3.13.17](https://github.com/python/cpython/blob/v3.13.17/Modules/getpath.py#L600)
consulta esse landmark por `isdir`; sua ausencia pode emitir diagnostico de
exec_prefix mesmo quando todos os modulos nativos retidos sao builtins.
Manter esse recurso nao reintroduz `_dbm`, `_tkinter` ou qualquer binario,
nem altera os hashes dos539 arquivos selecionados. O smoke remoto exige sua
presenca; stderr continua sendo recusado.

As entradas atuais possuem **19 notices verbatim e23 arquivos no manifesto
fechado** `python-license-review-manifest.json`, SHA
`d841ef9a6815f05635286ac205cd20e2f9a0a083b2ad777f3d285f09b813d6cf`:
19 notices, inventario, documentacao de licencas/mudancas e dois metadados
upstream. O resumo de mudancas PSF3/CNRI3 e o texto CWI estao em
[support-contact-runtime-licenses.md](support-contact-runtime-licenses.md).
Nenhum desses arquivos foi alterado pelo empacotador.

O upstream-PYTHON.json tem SHA
`6912dc06b4bad2af7602fe3366e22c3885defbc3a2d567f909d4c8528bcee69b`.
A proveniencia reutiliza V2 `1adb68dc7df6308a60471335f4e76c72a2fcc4bffcd5084d86af21e2ae8aa915`
e revisao independente `19dc08ed669ff2b38222abb7ab025348c936cb14f6aab1c621eb104cd1dbee96`.
A conclusao e limitada a projecao estatica revisada entre arquivos full/stripped,
sem identidade de bytes brutos ou qualificacao de runtime. Nao houve novo ELF scan.

## Contexto offline e imagem

`profile.py prepare` recebe somente arquivo CPython publico, diretorio dos tres
arquivos originais do helper, caminho explicito do runner e novo arquivo tar de
saida. Exige DLL `11419c7d93a94666d535b5b5cfa2205d2ac0d1f8d0c840297f3eaa2489b044c3`
e runner `87e09073a60a5c2ab12804c25160fa8d67c8cb53a297a052b0bd4d3733ab8211`.
Nao recebe captura, configuracao privada ou copia de diretorio arbitrario.

O parser limita64MiB compactados,512MiB expandidos e30000 membros, com teto para
metadados. Rejeita traversal, caminhos absolutos, duplicatas, hardlinks, devices,
FIFO e aliases nao declarados. Os1048 aliases exatos do arquivo oficial sao
validados contra manifesto pinado, resolvidos em ate64 passos dentro de python/
e materializados como arquivos regulares; ciclos e escapes sao recusados.
O inventario original contabiliza todos4528 nomes, inclusive colisões opcionais
de maiusculas/minusculas do filesystem do Mac. Arquivos retidos devem corresponder
aos bytes originais. O contexto tar ordena nomes e normaliza ownership/tempo/modo.

O contexto inclui fechamento/selecoes/inventario originais, os19 notices,
upstream-PYTHON, proveniencia V2/revisao, documentacao de mudancas e snapshot da
politica. Notices exigem nomes planos no prefixo fixo, tamanho/hash exatos, sem
symlink ou arquivo extra. O manifesto de construcao vincula esses materiais,
helper/runner e fontes. Nao existem fingerprints de inputs privados.

O Dockerfile faz apenas COPY e metadados sobre a base publica existente
`mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374`,
linux/amd64. Fixa PATH=/usr/bin:/bin e USER1654:1654. O entrypoint usa
`/opt/python/bin/python3.13 -I -B`, runner original e dotnet real em
`/usr/share/dotnet/dotnet`. Nao ha RUN/apt/pip/SDK/recompilacao.

## Schema2: proposta de admissao somente para perfil sintetico

Uma futura admissao exige estado `PROFILE_ONLY_ADMITTED`, scope
`synthetic_support_contact_profile` e cinco decisoes separadas:
`bzip2-1.0.6`, `X11`, `Zlib`, `SQLite-public-domain`, `Python-2.0-history-summary`.
Cada decisao deve indicar `PROFILE_ONLY_APPROVED`, hash de referencia do parecer
do Hub e hash de TODOS os bindings. O hash agregado do recibo, pinado no lock,
e a ancora de confianca; referencias individuais nao sao carregadas como novos
recibos. Booleans genericos, waiver global, casos ausentes ou recibo antigo
ADMITTED sao recusados. Nenhuma equivalencia SPDX ou excecao global e criada.

Os bindings incluem base/arquivo, selecao/closure, ambos binarios centrais,
helper/runner, inventario/notices, manifesto fechado, upstream-PYTHON, V2/revisao,
documentacao e hashes dos fontes. A politica permanece com SHA
`f2880c90e41e77c402703be4420ced16d3ed2ecc1ae91ade693999ec2774df7c`.
`qualify.profile_license_bindings(lock)` retorna somente o objeto publico NAO
ASSINADO para decisao do Hub; nao emite aprovacao ou altera o lock.

Antes de download/build o gate confere o recibo agregado e todos os bindings.
Depois do prepare compara fechamento, selecao, binarios, metadados e fontes
realmente construidos. Antes de exportar OCI, o executor verifica dentro da
imagem a presenca, bytes e SHA de cada material publico/notice e binario central.
Essa verificacao esta implementada e ainda nao observada numa imagem atual.

A primeira qualificacao apos a admissao terminou com `command_failed`, sem
diagnostico suficiente para atribuir causa. A execucao seguinte identificou
`python_smoke_start`, codigo0 e `diagnostics_observed`; nao publicou texto bruto.
A regressao local confirmou a remocao indevida do landmark vazio; a eliminacao
do diagnostico e o sucesso do runtime ainda dependem do novo CI remoto.
A falha registra somente fase
de uma lista fechada, codigo de saida, categoria fixa e, quando reconhecido,
basename de biblioteca de uma lista fixa. Ate4KiB de stderr sao inspecionados
apenas em RAM; seu texto, argumentos, ambiente e capturas nao sao preservados.
Os limites existentes de saida, tempo, recursos e limpeza permanecem iguais.

## Qualificacao e verificacao restante

O workflow dedicado usa Ubuntu24 hosted amd64, checkout/upload pinados,
contents:read e15minutos, sem secrets novos ou publicacao de registry. O codigo
verifica Python/ASP.NET reais e isolamento efetivo:1654:1654, rede none, raizRO,
cap-dropALL sem CapAdd, no-new-privileges,512MiB/swapigualmemoria,0.5CPU,64PIDs,
tmpfs privado16MiB, corezero e logdriver none. Esses tetos sao candidatos,
nao medicoes admitidas. O caller nao recebe socket/proc/PID do host.

Uma nova parelha mecanica sintetica usa o grafo publico WebC934 pinado para os
dois labels de role; o container fonte nunca inicia. Isso nao prova o grafo
Worker real nem configuracao operacional. Saidas sao fechadas e limitadas a16KiB;
o prazo e15s por chamada, com limpeza dos containers possuidos inclusive na falha.
OCI/config/layers e archiveSHA sao vinculados somente apos todas as verificacoes.

TDD do delta atual observou RED antes das funcoes de manifesto, bindings e
verificacao de imagem;14 testes de empacotamento e10 de qualificacao passaram
naquele snapshot. O delta posterior de diagnostico e a regressao de nao-mutacao
do lock foram verificados em3 testes direcionados; essa regressao cobre tanto
NOT_ADMITTED quanto PROFILE_ONLY_ADMITTED, sem pressupor estado permanente.
O delta do landmark observou RED com o diretorio ausente e GREEN em5 testes
afetados de selecao/contexto, incluindo modo/uid/gid/mtime e reproducibilidade.
Comandos locais: `env -i PATH=/usr/bin:/bin /usr/bin/python3 -B -m unittest discover
-s tools/support-contact-runtime -p test_profile.py -v` e o mesmo com
`-p test_qualification.py`. Somente fixtures/stdLib Python novas foram executadas.
Os15 testes antigos do runner,12 de IOptions, fullimage, CI anterior, C934,
SQL48/HTTPS/SMTP nao foram repetidos. Docker/.NET nao executaram no Mac.

O contexto historico de18 notices (`minimal-context-notices.tar`,76,564,480B,
SHA374f171b751b2f17037a637e7d8ba1770025a3c6644dc72f5ae57573daa0ce67)
comprova somente aquele snapshot de preparo. Nao representa os19notices/23arquivos
atuais. O contexto atual nao foi regenerado; o fechamento Python anterior foi
reutilizado pois os novos materiais ficam fora dele. Qualificacao sintetica,
admissao operacional e fechamento das #68/#82/#93 continuam separados e pendentes.
