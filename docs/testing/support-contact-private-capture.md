# Captura privada de SupportContact: fornecedor e chamador candidatos

Escopo: fatia existente S0–S4 de #82, sob janela exclusiva do operador69. Estes arquivos implementam uma fronteira candidata; testes de fixtures não qualificam imagem, coletor Linux, montagem, processo real nem aceite operacional. Nenhum agente autor deste código executa Docker, .NET, SSH ou captura real no Mac. #68, #82 completa e #93/M5 permanecem separados.

A implementação não compila nem busca artefatos. Preserva `run_preserved_probe.py` SHA256 `87e09073a60a5c2ab12804c25160fa8d67c8cb53a297a052b0bd4d3733ab8211`, DLL `11419c7d93a94666d535b5b5cfa2205d2ac0d1f8d0c840297f3eaa2489b044c3`, artefato canônico12589441146. O runner original mantém 256KiB, saída combinada16KiB, subprocesso15s e ambiente inicial limpo do CLR; a captura entra pelo stdin e seus valores Environment são reconstruídos pelo helper após o início do CLR.

## Fronteira pública de admissão

`capture.load_admission(raw_bytes, expected_sha256)` exige o SHA256 público independente do manifesto completo. Esse pin é fornecido pelo operador após revisão das evidências; calcular o hash de um JSON arbitrário não o transforma em evidência qualificada. O manifesto é exclusivamente público, imutável e sem PID, caminho, comando ou valor de captura. Chaves duplicadas, extras, NULL, booleanos no lugar de inteiros e hashes inválidos recusam a admissão. Não há bypass.

Contrato exato, versão1:

- `version`: inteiro1; `pins_sha256`: `9c779cebc3dc3af2a0b0c0c945fef86460210bd19bcd6187f68e0a6e536bf128`.
- `reader_uid`: UID não root que já possui a autoridade necessária; `exclusive_operator`: inteiro69; `handoff_sha256`: recibo público da janela exclusiva. Nada concede autoridade ou muda permissões.
- `profile`: `image_digest` e `config_digest` no formato `sha256:<64hex>`; `qualification_receipt_sha256`, `python_closure_sha256`, `independent_review_sha256`, `capture_source_sha256`, `caller_source_sha256` com64hex; `public_environment` como lista exata de strings públicas `NAME=value` do Config.Env da imagem qualificada. Hashes dos dois fontes são conferidos. O ConfigID qualificado vincula a configuração completa da imagem.
- `roles`: exatamente `web` e `worker`; cada um contém exatamente `startup_stability_receipt_sha256` e `mounted_preimage_receipt_sha256`. Recibos independentes devem vincular o container/startup C934 fixado, o mesmo grafo app/config e estabilidade de todas as fontes aplicáveis desde startup. Estes fatos não são inferidos de um instante Docker. Os recibos não contêm nem fazem hash de valores/configuração privados.

O ambiente público do perfil aceita apenas PATH, APP_UID, ASPNET_VERSION, DOTNET_VERSION, DOTNET_RUNNING_IN_CONTAINER, ASPNETCORE_HTTP_PORTS e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT, com PATH `/usr/bin:/bin`, sem duplicatas. Também aceita exatamente os controles declarados no Dockerfile: DOTNET_NOLOGO=true, DOTNET_CLI_TELEMETRY_OPTOUT=true, DOTNET_EnableDiagnostics=0 e COMPlus_DbgEnableMiniDump=0; valores diferentes recusam. Hooks, preload e variáveis de bootstrap não são admitidos. Ambientes públicos diferentes exigem revisão do contrato; nunca são filtrados silenciosamente. O ambiente efetivo do processo real é capturado de `/proc`, e não de Docker Config.Env.

NULL para qualquer pin ou recibo continua sendo STOP real. O estado atual não possui admissão qualificada, recibos novos nem captura operacional. A admissão vazia/default retorna `UNKNOWN_RUNTIME_PROFILE` antes de Docker ou leitura de `/proc`.

## Seleção e identidade antes de ler dados privados

`capture.py` contém somente dois alvos fixos: os containerIDs, referências raiz, folhasAMD64, ConfigIDs, fonte `c9341ab39a5e3133067eb579021efdd8c19b0033`, StartedAt e assemblies de C934 já registrados. Não há descoberta do container mais recente, seleção por PID ou CLI arbitrária. A folha nativa é inspecionada pela referência completa fixa ghcr.io/bipoxdxd/duora-{papel}@sha256:folha; um digest de manifesto isolado não é usado como ImageID Docker. A rota Docker usa apenas inspeção formatada dos campos necessários, sem dump de Env/configuração da aplicação.

`LinuxAuthority` exige Linux e o UID já admitido. Abre uma única vez o diretório `/proc/<pid>` selecionado da inspeção do container fixado, mantém esse FD e usa leituras relativas com `O_NOFOLLOW`. Confere o campo22 de stat, inclusive nomes com espaços/parênteses, antes e depois da leitura. Um PID reciclado não é reaberto. PID, bootID, kernel-start, fonte, imagem, startup e metadados de mounts devem permanecer idênticos. Cwd deve ser `/app`, comando deve ser o dotnet real com o assembly fixo; supervisores, apphost desconhecido ou contexto diferente recusam.

O root do processo é a única resolução intencional de magic-link de `/proc`. Os descendentes `app`, `run` e `secrets` são abertos como diretórios sem seguir symlinks. Identidades device/inode de app e secrets são comparadas antes/depois e com os diretórios efetivamente montados no sandbox. Rootfs de origem e mounts que sobreponham app/config devem ser somente leitura; ausência desse requisito conserva UNKNOWN. Isso não prova imutabilidade desde startup: os dois recibos continuam obrigatórios.

## RAM, isolamento e limites

Antes de qualquer leitura privada, o chamador exige host Linux sem swap, bloqueio de RAM `mlockall`, dumpability desligada e core0, sem elevar limite ou permissão. O host sem swap também cobre buffers do cliente/daemon Docker, pois exec não herda locks de memória. A qualificação externa deve demonstrar que o daemon/host já não registra nem produz dumps desses dados; o código não muda a política do daemon. Se a autoridade existente não satisfaz os controles, STOP. O fornecedor filho reaplica o bloqueio antes da leitura privada. Ele cria sua própria sessão/grupo antes de qualquer trabalho; os comandos Docker de metadados permanecem nesse grupo. A limpeza sempre termina e verifica o grupo inteiro, inclusive após saída do líder/EOF. Grupo ou reap sem confirmação conserva SUPPLIER_CLEANUP_UNCONFIRMED, nunca limpeza positiva.

O sandbox é criado com nome aleatório próprio, sem pull, UID/GID1654, rede none, rootfs somente leitura, cap-drop ALL e sem cap-add, no-new-privileges, log-driver none, memória512MiB com swap igual à memória, CPU0.5, pids64, core0 e tmpfs privado16MiB. O chamador confere a configuração efetiva e ConfigID; não basta ter enviado flags. O ambiente inicial do cliente Docker é fixo, sem autenticação/contexto herdado. O sandbox usa o ambiente público exato da imagem qualificada.

Entrypoint fixo: `/opt/python/bin/python3.13 -I -B /opt/probe/run_preserved_probe.py --artifact-dir /opt/probe/artifact --dotnet /usr/share/dotnet/dotnet`. São os binários reais qualificados, sem shim ou alias.

Há somente dois bind mounts, ambos readonly e rprivate: `/proc/<pid-verificado>/root/app` → `/app`; `/proc/<pid-verificado>/root/run/secrets` → `/run/secrets`. Nenhum socket, namespace PID/proc de host, volume ou diretório adicional entra no sandbox. O coletor inicia o sandbox aguardando stdin, confere os diretórios efetivamente montados e só então coleta. A identidade de origem é reconferida antes e depois da captura e após o replay. Qualquer lacuna de acesso ou prova de mesma preimagem interrompe a chamada.

O JSON privado contém exatamente Role, ApplicationAssembly, ContentRoot, Environment e Arguments. O fornecedor não produz stdout; entrega bytes somente pelo pipe anônimo privado até EOF. Não grava, loga ou faz hash desses valores. Strings inválidas, env duplicado, UTF8 inválido ou captura maior que256KiB recusam. Environment e argumentos não são reescritos para produzir match.

O fornecedor tem5s incluindo EOF e término do filho. O chamador tem25s totais, separados do limite original15s do runner; limpeza possui orçamento próprio5s. Um papel é tentado uma vez por Session; a entrada operacional executa no máximo web e worker, parando no primeiro UNKNOWN. Não existe retry automático. A janela exclusiva deve impedir nova invocação por outro processo; o código não cria estado privado persistente para coordenação.

Timeout, EOF ou desconexão não provam limpeza. O coletor filho deve ser terminado/recolhido e o container próprio removido; a ausência do nome é confirmada por nova listagem Docker bem-sucedida. Falha de término do fornecedor/cliente continua `CLEANUP_UNCONFIRMED`, mesmo com remoção confirmada do container. Nunca mata Web/Worker/PG.

## Saída e verificação

A saída pública é reconstruída de um esquema fechado: enums, booleanos, source enum e MVID. Não há Docker/.NET stdout/stderr bruto, mensagem de exceção, valor privado ou fingerprint privado. Flags cached_runtime_options_observed, runtime_effective_proven, application_readiness_observed, smtp_observed, outbox_observed e m5_observed ficam sempre false. Um replay compatível é `REPLAY_VERIFIED`; a conclusão operacional agregada de estabilidade/configuration-binding pertence ao recibo69, sem alegação de cache vivo.

Foram executados somente os novos testes de stdlib, com dados sintéticos, sob ambiente limpo:

```
env -i PATH=/usr/bin:/bin /usr/bin/python3 -B -m unittest discover -s tools/support-contact-runtime -p 'test_capture.py'
env -i PATH=/usr/bin:/bin /usr/bin/python3 -B -m unittest discover -s tools/support-contact-runtime -p 'test_ram_caller.py'
```

O ciclo inicial registrou8/6 falhas esperadas por ausência da implementação; os ciclos seguintes reproduziram a ausência de comparação de mounts/EOF, o falso aceite de limpeza de fornecedor e o aceite indevido de CapAdd. Todos passaram após as correções. Os testes cobrem parsing/limites/identity mismatch/PID reuse antes e depois/ausência de testemunhos/pipe real sem EOF/saída fechada/controles de inspeção de sandbox. Não constituem teste de Docker, `/proc` real, mlock, isolamento, CLR, runtimeASP.NET ou produção.

Não foram repetidos os15 testes do runner,12 IOptions, CI antiga, outboxlab, SMTP, providers ou os gates do produto. Build e qualificação remota do novo perfil, os limites de execução/limpeza reais e uma execução mecânica sintética com o helper preservado continuam pendentes e precisam de recibo independente antes da admissão69. O staging do código/runner/artefato deverá ser imutável e corresponder aos pins revisados.
