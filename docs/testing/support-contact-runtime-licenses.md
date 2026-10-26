# Licenças e proveniência do candidato Python de SupportContact

**Estado: NOTICE_TEXTS_SUPPLEMENTED_POLICY_REVIEW_AND_EXACT_NATIVE_METADATA_PENDING.** Há18 textos legais oficiais copiados integralmente em notices. O fechamento exato da distribuição, a inclusão dos avisos na imagem e a revisão da política interna continuam pendentes. Ausência de um ID na allowlist não significa ausência de permissão legal.

O [inventário JSON](../../tools/support-contact-runtime/python-license-inventory.json) preserva os45 caminhos relacionados a licença do arquivo install_only (42 textos, AUTHORS e2 arquivos de código/dados), suas dimensões e SHA256 integrais. Preserva também22 fontes oficiais pequenas e os18 avisos suplementares, cada um com caminho de origem, commit, Git blob e SHA256. Nenhum binário foi executado ou extraído; não houve novo teste/runtime/Docker/CI/produção.

Distribuição examinada: release20261021 de astral-sh/python-build-standalone, asset663545914,35.356.674bytes, SHA256 `f829adc9d888e76c8801b4799ed444188d35239fcae3a9808a99ec5cb0d8d92c`. O [tag oficial resolve ao commit](https://github.com/astral-sh/python-build-standalone/commit/0c5e581b54a4736cdbbe68bdd6d7e571a1dca11e) `0c5e581b54a4736cdbbe68bdd6d7e571a1dca11e`. Os22 textos retornados pelo GitHub App tiveram os bytes conferidos com seus blobsGit.

## Três distinções necessárias

**Avisos ausentes da embalagem.** A [documentação oficial](https://github.com/astral-sh/python-build-standalone/blob/0c5e581b54a4736cdbbe68bdd6d7e571a1dca11e/docs/distributions.md) explica que install_only preserva python/install e omite PYTHON.json e os demais arquivos do full. Confirmamos que este arquivo não contém PYTHON.json nem python/licenses. A falta de textos nativos conhecidos foi corrigida no repositório pela cópia literal dos avisos oficiais em [notices](../../tools/support-contact-runtime/notices). Ainda falta demonstrar que esses textos entram na imagem candidata; não foi alterado Dockerfile/contexto nesta tarefa.

**Concessão condicionada e cumprimento dos termos.** Os textos Sleepycat, bzip2, X11, TCL e Zlib concedem uso/redistribuição sob condições. Copiar os avisos preserva termos e atribuições; não é uma nova aprovação e não comprova automaticamente seu cumprimento integral. Sleepycat, em especial, exige informação para obter fontes de DB e do software acompanhante que o utiliza, conforme os termos exatos. Não há recibo de oferta de fontes/cumprimento dessa condição. Também não há prova de todas as condições da closure inteira. Isso é diferente de afirmar que nenhuma permissão existe.

**Revisão interna da allowlist.** A [política atual](../../scripts/supply-chain/license-policy.json), SHA256 `f2880c90e41e77c402703be4420ced16d3ed2ecc1ae91ade693999ec2774df7c`, permite Python-2.0; não lista literalmente PSF-2.0, CNRI-Python, Sleepycat, bzip2-1.0.6, X11, TCL, Zlib ou MPL-2.0 geral. Existem exceções MPL para outros nomes, sem cobrir certifi deste bundle. Os IDs fora da política pedem revisão interna; esta tarefa não altera allowlist/exceções e não conclui falta de permissão legal.

CPython: o header instalado confirma3.13.17; o texto inclui PSF/BeOpen/CNRI/CWI e documentação0BSD. O catálogo oficial declara Python-2.0 e CNRI-Python. Python-2.0 já está permitida. A história CNRI/PSF não é tratada como obrigação adicional separada sem termo que o demonstre; a expressão global permanece NULL até a reconciliação exata, sem substituir tudo por PSF-only.

## Tabela factual do arquivo e da receita oficial

Na coluna versão, **header** significa confirmação no arquivo instalado; **receita** é a versão declarada em downloads.json do commit acima, sem confirmação da versão exata do componente binário. Flags lidas por AST literal_eval de `_sysconfigdata__linux_x86_64-linux-gnu.py` comprovam presença/linkagem dos componentes, sem executar esse arquivo. Os textos da coluna aviso estão copiados integralmente em notices; os hashes por arquivo constam do JSON.

| Componente | Versão e prova | Licença declarada no fornecedor | Aviso suplementar | Artefato/fato ainda ausente |
|---|---|---|---|---|
| cpython-3.13 | 3.13.17 (header) | Python-2.0, CNRI-Python | LICENSE.cpython.txt | Inclusão dos avisos na imagem; mapeamento completo do PYTHON.json exato |
| bdb | 6.0.19 (receita) | Sleepycat | LICENSE.bdb.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem; evidência de oferta de fontes/cumprimento Sleepycat |
| bzip2 | 1.0.8 (receita) | bzip2-1.0.6 | LICENSE.bzip2.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| expat | 2.9.0 (receita) | MIT | LICENSE.expat.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| libedit | 20240808-3.1 (receita) | BSD-3-Clause | LICENSE.libedit.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| libffi | 3.8.0 (receita) | MIT | LICENSE.libffi.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| libX11 | 1.8.13 (receita) | MIT, X11 | LICENSE.libX11.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| libXau | 1.0.12 (receita) | MIT | LICENSE.libXau.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| libxcb | 1.17.0 (receita) | MIT | LICENSE.libxcb.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| mpdecimal | 4.0.0 (receita) | BSD-2-Clause | LICENSE.mpdecimal.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| ncurses | 6.5 (receita) | X11 | LICENSE.ncurses.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| openssl-3.5 | 3.5.9 (receita) | Apache-2.0 | LICENSE.openssl-3.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| sqlite | 3530100 (receita) | domínio público declarado; sem ID SPDX | LICENSE.sqlite.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| tcl | 9.0.4 (receita) | TCL | LICENSE.tcl.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| tk | 9.0.4 (receita) | TCL | LICENSE.tcl.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| uuid | 1.0.3 (receita) | BSD-3-Clause | LICENSE.libuuid.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| xz | 5.8.4 (receita) | 0BSD | LICENSE.liblzma.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| zlib | 1.3.2 (receita) | Zlib | LICENSE.zlib.txt | PYTHON.json exato para versão/link/licença; inclusão dos avisos na imagem |
| pip / certifi vendorizado | pip26.2.2 / certifi2026.10.19, METADATA/vendor.txt presentes no arquivo | MIT / MPL-2.0 no LICENSE de certifi | Textos já no arquivo; MPL-2.0.txt suplementa o texto geral | Inclusão dos avisos na imagem e revisão interna MPL específica para este componente |

A presença de Berkeley DB é concreta: `_dbm` compartilhado consta do tar, CONFIG_ARGS declara `--with-dbmliborder=bdb` e MODULE__DBM_LDFLAGS contém `-ldb`. A versão6.0.19 e Sleepycat são declarações da receita oficial, sem inventar um recibo binário ausente. Não foi removido qualquer builtin ou biblioteca estática.

Packaging concede alternativa Apache-2.0 OR BSD-2-Clause. A tabela de IDs SPDX de packaging não é tratada como licença de todos os seus identificadores. O LICENSE geral do repositório de construção, copiado como MPL-2.0.txt, identifica os termos desse sistema e fornece o texto completo MPL; isso não rotula todo o runtime como MPL.

## Lacuna exata que impede fechar a closure

Falta **PYTHON.json da distribuição full correspondente exatamente ao arquivo install_only examinado**, que traz o mapeamento de construção/linkagem/licenças. Sua omissão é característica documentada do formato. O catálogo geral de fontes em downloads.json não substitui esse recibo exato. A leitura do arquivo confirma Tcl itcl/thread, enquanto CONFIG_ARGS declara mimalloc/JIT e MODLIBS referencia HACL; o mapeamento integral de termos/atribuições desses componentes e da closure de sistema não foi provado.

A disponibilidade dos18 textos conhecidos no repositório está reparada. A prova da inclusão na imagem, a closure exata, condições de redistribuição aplicáveis e a revisão interna dos IDs faltantes continuam separadas e explícitas no JSON. A distribuição não recebe admissão automática por ter origem oficial ou hash correto. Não há alteração de política, waiver, nova aprovação ou alegação de aceite operacional de #82/#68/#93.
