# Licenças e proveniência do candidato Python de SupportContact

**Estado: SOURCE_NOTICES_AND_CHANGE_SUMMARY_COMPLETE_STATIC_PROVENANCE_REVIEWED_NO_ADMISSION.** O PYTHON.json oficial completo e os avisos nativos reais agora estão disponíveis. A revisão específica da política do candidato, a prova de inclusão dos avisos na imagem e a qualificação operacional continuam separadas. Ausência de ID na allowlist não significa ausência de permissão legal.

## Fontes concretas e fronteira da evidência

- Entrada selecionada: asset663545914, cpython3.13.17 install_only_stripped,35.356.674bytes, SHA256 `f829adc9d888e76c8801b4799ed444188d35239fcae3a9808a99ec5cb0d8d92c`. O arquivo original não foi alterado.
- Metadados completos: asset oficial663545916, pgo+lto full,112.991.216bytes, SHA256 `344eec7e761e7b3dccce0ce2d91a3e70d40f8ff5d3ce7d907d979c23e8451fc7`. A extração limitada já fornecida pelo coordenador leu metadados/avisos sem executar seus binários.
- [upstream-PYTHON.json](../../tools/support-contact-runtime/upstream-PYTHON.json) é cópia byte a byte de python/PYTHON.json:127.803bytes, SHA256 `6912dc06b4bad2af7602fe3366e22c3885defbc3a2d567f909d4c8528bcee69b`. A lacuna anterior desse arquivo está resolvida.
- [upstream-distribution-provenance.json](../../tools/support-contact-runtime/upstream-distribution-provenance.json) preserva origem dos assets, pins, recibos dos avisos e inittab/config.c. O [inventário](../../tools/support-contact-runtime/python-license-inventory.json) contém todos os caminhos, tamanhos e hashes integrais, além do mapeamento por módulo/link/licença.

O [commit oficial20261021](https://github.com/astral-sh/python-build-standalone/commit/0c5e581b54a4736cdbbe68bdd6d7e571a1dca11e) fixa as receitas de fontes. As versões por biblioteca continuam marcadas como **declaradas na receita**, salvo confirmação própria de metadado/header: o mapa de links não é uma chamada de API de versão. CPython3.13.17 consta de PYTHON.json e do header instalado.

O recibo estático V1 permanece preservado: raw files diferem e a normalização V1 não igualou todos os segmentos do interpretador. O V2 fornecido pelo coordenador reporta igualdade dos payloads carregados/grafo virtual dos dois binários, após normalização explicitamente limitada a offsets físicos/campos de layout ELF; SHA256 do recibo `1adb68dc7df6308a60471335f4e76c72a2fcc4bffcd5084d86af21e2ae8aa915`. A revisão independente de full-distribution-provenance-review.md, SHA256 `19dc08ed669ff2b38222abb7ab025348c936cb14f6aab1c621eb104cd1dbee96`, é favorável à projeção estática dos artefatos fixados, sem P1/P2 demonstrado. O executável tem GNU_build_ids=[] dos dois lados: igualdade de listas vazias não é prova positiva de build ID. Somente libpython contém o build ID positivo coincidente `f25081008d673f28ea14aedaab871dc3dcd6239d`. A conclusão para ambos usa os segmentos normalizados. Nenhuma identidade de SHA256 bruto ou execução bem-sucedida é afirmada.

## Arquivo completo versus closure selecionada

O inittab real de config.c contém96 módulos, exatamente os96 módulos não compartilhados de PYTHON.json. Os14 registros builtin que declaram bibliotecas externas mapeiam13 arquivos estáticos distintos e13 caminhos de avisos, incluindo o aviso do CPython. Todos esses13 avisos estão disponíveis nos bytes exatos do full. O inventário mantém os96 registros e os links de sistema, sem inferir versões adicionais.

`_dbm` e `_tkinter` aparecem como shared_lib em PYTHON.json e não aparecem no inittab. A política de seleção exclui seus arquivos compartilhados e os payloads Tcl/Tk auxiliares. Exclui também site-packages, pip/certifi, ensurepip e pacotes opcionais de teste/interface. Portanto, Sleepycat/DB, TCL/libX11 de `_tkinter` e MPL de certifi pertencem à entrada completa e não são usados como bloqueadores da closure selecionada. Os termos do arquivo de entrada continuam inventariados. A licença X11 permanece no candidato por ncurses; não confundir essa licença com a biblioteca libX11 excluída.

Remover esses arquivos **não remove builtins**. `_bz2`, `_curses`, `_curses_panel`, readline, binascii, zlib, `_sqlite3`, `_ssl` e outros módulos não compartilhados continuam incorporados nos binários selecionados. Não houve recompilação nem poda de módulos compilados.

| Componente na closure selecionada | Módulos/links reais do PYTHON.json | Versão na receita oficial | Licença declarada e aviso real |
|---|---|---|---|
| CPython | core e96 módulos inittab | 3.13.17 confirmada em metadados/header | Python-2.0/CNRI-Python declarados; LICENSE.cpython.txt |
| bzip2 | _bz2 → build/lib/libbz2.a | 1.0.8, receita | bzip2-1.0.6; LICENSE.bzip2.txt |
| libffi | _ctypes → build/lib/libffi.a | 3.8.0, receita | MIT; LICENSE.libffi.txt |
| ncurses | _curses/_curses_panel/readline → libncursesw.a/libpanelw.a | 6.5, receita | X11; LICENSE.ncurses.txt |
| libedit | readline → build/lib/libedit.a | 20240808-3.1, receita | BSD-3-Clause; LICENSE.libedit.txt |
| mpdecimal | _decimal → build/lib/libmpdec.a | 4.0.0, receita | BSD-2-Clause; LICENSE.mpdecimal.txt |
| OpenSSL | _hashlib/_ssl → libcrypto.a/libssl.a | 3.5.9, receita | Apache-2.0; LICENSE.openssl-3.txt |
| liblzma/xz | _lzma → build/lib/liblzma.a | 5.8.4, receita | 0BSD; LICENSE.liblzma.txt |
| SQLite | _sqlite3 → build/lib/libsqlite3.a | 3.53.1.0 declarada pela receita (campo actual_version), não observada por API | Domínio público; LICENSE.sqlite.txt |
| libuuid | _uuid → build/lib/libuuid.a | 1.0.3, receita | BSD-3-Clause; LICENSE.libuuid.txt |
| expat | pyexpat → build/lib/libexpat.a | 2.9.0, receita | MIT; LICENSE.expat.txt |
| zlib / aviso zlib-ng | binascii/zlib → build/lib/libz.a; metadata exige ambos avisos | 1.3.2 zlib e2.2.4 zlib-ng declaradas nas receitas; implementação/versão API não inferida do nome libz.a | Zlib; LICENSE.zlib.txt + LICENSE.zlib-ng.txt |

## Avisos reparados e condições observadas

Dos18 avisos nativos extraídos do full,17 já coincidiam byte a byte com os textos oficiais copiados anteriormente; nenhum de seus hashes foi trocado. LICENSE.zlib-ng.txt faltava e foi acrescentado com SHA256 `6c9f0d975b41afaa34d22f55bb8986ce69e5cb7ad327cb2b28820cd425edf5ee`,867bytes. O texto suplementar MPL-2.0.txt foi preservado; o diretório [notices](../../tools/support-contact-runtime/notices) passa a conter19 arquivos,18 nativos exatos e esse suplemento. Cada arquivo possui proveniência e hash no JSON. A cópia em fonte/contexto não é tratada como prova de inclusão numa imagem construída.

As concessões observadas para os casos específicos do candidato são:

| Caso e módulo | Concessão e condições concretas | Estado de revisão |
|---|---|---|
| bzip2-1.0.6 / `_bz2` | Permite uso e redistribuição em fonte/binário, com ou sem alteração. Em fonte, preservar copyright/condições/disclaimer; não falsear origem/autoria; marcar fontes alteradas; não usar o autor para endosso/promoção sem autorização escrita. Agradecimento no produto é apreciado, não exigido. | Texto integral preservado; ID fora da allowlist, revisão interna pendente. |
| X11 de ncurses / `_curses`, `_curses_panel`, readline | Permite uso, cópia, modificação, distribuição, sublicença e venda. Preservar copyright e permissão em cópias/partes substanciais; não usar titulares em publicidade/promoção sem autorização escrita. Não foi observada condição de publicar fontes nesse texto. | Texto integral preservado; ID fora da allowlist, revisão interna pendente. |
| Zlib / binascii e zlib | Permite uso para qualquer fim, incluindo comercial, alteração e redistribuição. Não falsear origem; marcar fontes alteradas; não remover/alterar o aviso em distribuições de fonte. Agradecimento no produto não é exigido. | Ambos avisos exigidos pelo metadado preservados; ID fora da allowlist, revisão interna pendente. |
| SQLite / `_sqlite3` | Texto dedica código/documentação entregáveis ao domínio público e permite copiar, modificar, publicar, usar, compilar, vender e distribuir em fonte/binário, para finalidade comercial ou não comercial. Distingue scripts de build de outros termos que não chegam à biblioteca final. | Sem conversão automática para Unlicense/CC0; correspondência explícita na política pendente. |
| Composto histórico do CPython | Python-2.0 já está permitida. PYTHON.json declara também CNRI-Python; os textos completos PSF/BeOpen/CNRI/CWI são retidos. PSF§3 e CNRI§3 exigem resumo breve de alterações em obra derivada; CWI exige copyright/permissão na documentação de suporte. Estas obrigações são textuais, não inferidas apenas do histórico. | Resumo factual incluído abaixo; CWI reproduzido integralmente na documentação. Sem admissão ou afirmação de falta de permissão. |

A [política vigente](../../scripts/supply-chain/license-policy.json), SHA256 `f2880c90e41e77c402703be4420ced16d3ed2ecc1ae91ade693999ec2774df7c`, continua inalterada. Estes grants condicionados não são ausência de permissão. O estado LICENSE_NOT_ADMITTED e os pins NULL continuam sendo limites técnicos reais até a revisão/recibo correspondente; este documento não cria waiver, exceção ou aprovação.

## O que foi fechado e o que permanece separado

O mapeamento módulo → biblioteca estática → licença/aviso foi fechado na granularidade declarada pelo fornecedor para os96 módulos não compartilhados. O PYTHON.json que faltava e os18 avisos nativos reais estão preservados. Os casos específicos de política da closure selecionada são bzip2-1.0.6, X11/ncurses, Zlib, domínio público SQLite e reconciliação histórica do CPython; não são uma lista genérica de dependências desconhecidas.

O vínculo estático ELF V2 recebeu revisão favorável para os artefatos fixados. Permanecem separadas: decisão interna dos casos acima, confirmação de avisos e desta documentação na imagem, qualificação real do perfil e aceite operacional. Versões individuais das bibliotecas são declaradas na receita, sem fabricação de respostas de APIs. Nenhum teste antigo, binário distribuído, Docker, CI ou produção foi executado por esta atualização de inventário. Nenhum gate/política foi reduzido e #82/#68/#93 não foram encerrados.

## Resumo de alterações e retenção integral — PSF§3 / CNRI§3

Os textos integrais de PSF, BeOpen, CNRI e CWI, seus copyrights, permissões e disclaimers são preservados sem alteração em [LICENSE.cpython.txt](../../tools/support-contact-runtime/notices/LICENSE.cpython.txt), SHA256 `86e61415828a8b5b06ec8d024e6f086ce155a8b85fd0c419c0ba4dc004e74fdd`. O resumo abaixo integra a documentação de suporte da distribuição candidata; não substitui nenhum texto legal.

The upstream producer supplied CPython 3.13.17 built with pgo+lto and its official install_only_stripped distribution. The project did not rebuild CPython, change native source, patch compiled code, or remove compiled-in modules. It curated the verified distribution by retaining the two core binaries, permitted pure-standard-library files and selected required runtime files, and excluding optional packages, tests, site-packages/pip/certifi, Tcl/Tk auxiliary payloads and the shared _dbm/_tkinter files under the declared selection policy. It supplemented the official legal notices and public provenance metadata. Upstream stripping/repacking changes file layout and removes non-runtime/debug material; the accepted static projection supports compiled loadable-payload provenance, not equality of raw file hashes or a runtime execution claim.

Esta curadoria é registrada para cumprir, na fonte/documentação, o resumo breve exigido por PSF§3/CNRI§3. Não houve recompilação nem edição do código nativo pelo projeto. A presença deste documento numa imagem/distribuição efetiva ainda precisa ser demonstrada; não é inferida da cópia no repositório.

## Aviso CWI integral na documentação de suporte

O texto a seguir é reproduzido integralmente da seção CWI do aviso oficial preservado. Não há uso do nome CWI para endosso/publicidade.

```text
CWI LICENSE AGREEMENT FOR PYTHON 0.9.0 THROUGH 1.2
--------------------------------------------------

Copyright (c) 1991 - 1995, Stichting Mathematisch Centrum Amsterdam,
The Netherlands.  All rights reserved.

Permission to use, copy, modify, and distribute this software and its
documentation for any purpose and without fee is hereby granted,
provided that the above copyright notice appear in all copies and that
both that copyright notice and this permission notice appear in
supporting documentation, and that the name of Stichting Mathematisch
Centrum or CWI not be used in advertising or publicity pertaining to
distribution of the software without specific, written prior
permission.

STICHTING MATHEMATISCH CENTRUM DISCLAIMS ALL WARRANTIES WITH REGARD TO
THIS SOFTWARE, INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS, IN NO EVENT SHALL STICHTING MATHEMATISCH CENTRUM BE LIABLE
FOR ANY SPECIAL, INDIRECT OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT
OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Atos propostos para a revisão delimitada

A revisão pode considerar uso, cópia, curadoria/seleção de arquivos existentes e distribuição do candidato para qualificação sintética isolada, preservando avisos e documentação de suporte. Não é uma admissão emitida por este documento; não abrange produção, redes externas ou captura real. Dos19 avisos preservados,13 são exigidos pelo metadado da closure selecionada e6 são suplementos/avisos da entrada completa. Preservar esses6 textos não rotula seus componentes como dependências efetivas do candidato.
