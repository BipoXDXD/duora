# Harness C2: carga, backup e recuperação na VPS atual

Referências: issue #86, [capacity.md](capacity.md) (protocolo e critérios), [backup-restore.md](backup-restore.md),
coletor C1 `scripts/deploy/capacity_inventory.py`. Este documento descreve a ferramenta e o roteiro da janela.
**Nada aqui é resultado medido.** Executar só em janela autorizada pelo responsável.

## O que o harness faz

| Peça | Onde roda | Papel |
|---|---|---|
| `scripts/deploy/c2_shadow.py` | VPS, root | Sobe o projeto sombra `duora-c2`: a release corrente (digests do env da release), os limites e o hardening do `compose.prod.yml`, com segredos, banco, volumes, redes e repositório pgBackRest **sintéticos** (MinIO do drill). Semeia contas e eventos sintéticos, gera a fixture do restic e faz o restore do ensaio (RPO/RTO). |
| `scripts/deploy/c2_probe.py` | VPS, por stdin via SSH | Sampler somente leitura a cada 5 s: CPU/steal/iowait, PSI, MemAvailable, swap, OOM, discos, uso de filesystem, CPU/throttling/memória/PSI/I/O de **todos** os containers (sombra e outros apps) e das slices indicadas, e do sombra o arquivamento de WAL, `ops.jobs` e outbox. Dois comandos exatos em allowlist; recusa o projeto `duora`. |
| `scripts/deploy/c2_load.py` | Mac | Participantes sintéticos (uma thread e uma conexão keep-alive cada, cookies e CSRF como a SPA) pelo túnel SSH, com TLS verificado contra a raiz interna do Caddy do sombra. |
| `scripts/deploy/c2_harness.py` | Mac | Executa as fases, dispara FULL e DIFF na VPS, avalia os guardas a cada segundo, aborta e grava evidência e relatório. |

Dois perfis (`python3 scripts/deploy/c2_harness.py plan --profile <perfil>`):

- **`janela`** (padrão; decisão do responsável em 28/10/2026: a janela inteira na VPS, com a #174, cabe em 2–3 h):
  linha de base (2 min), aquecimento (10 clientes, 2 min), rampa 10/25/50/100 (2 min cada), pico 100 por 10 min e
  **3 ciclos** a 100 clientes de entrada (100 logins em 60 s, fase de 90 s), 3 reconexões de 50% em 10 s (45 s
  cada), **FULL** (mínimo 4 min e até o backup terminar, prazo 10 min), intervalo de 2 min e **DIFF** + restic (mesma
  regra), e recuperação final de 5 min a 10 clientes. Nominal 68 min, 100 clientes contínuos por 53 min, uma
  execução (`--repetition 1`); o guarda `WINDOW` encerra a execução em 80 min.
- **`completo`** (protocolo original de [capacity.md](capacity.md), para host isolado ou janela longa): linha de
  base (2 min), aquecimento (5 min), rampa de 5 min por degrau, pico 100 por 30 min, entrada, 3 reconexões,
  recuperação de 15 min, FULL (mínimo 30 min), intervalo de 10 min a 10 clientes, DIFF + restic e recuperação de
  15 min. Nominal 162 min, prazo de 310 min por repetição, 3 repetições.

Fases não são truncadas: fase de backup que passa do prazo aborta (`DEADLINE`).

### Reduções do perfil `janela` e por que o resultado continua válido

| Protocolo (capacity.md) | `janela` | Por que continua válido |
|---|---|---|
| 3 repetições completas | 1 execução com 3 ciclos de contenção | Cada ciclo repete entrada, 3 reconexões, FULL e DIFF; a variação entre ciclos é a variação entre repetições. Só a rampa e a recuperação final têm uma medição. |
| Aquecimento de 5 min | 2 min | Os degraus 10 e 25 continuam aquecendo (JIT, pools, cache); nenhum gate do pico é lido antes de 10 min de tráfego. |
| Degraus de 5 min | 2 min | A 100 clientes, cerca de 30 req/s: 3.600 requisições e 24 amostras por degrau, o bastante para p95 e para ver o joelho. |
| Pico de 30 min | 10 min, seguido dos 3 ciclos a 100 | 53 min contínuos a 100 clientes, acima dos 30 min (vazamento, autovacuum, filas). |
| FULL/DIFF com mínimo de 30 min | mínimo de 4 min e até terminar; prazo 10 min | O banco sintético é pequeno (o da produção tinha 11 MB em 28/10) e o backup termina em segundos; o resto da fase mede a cauda de WAL e do arquivamento sob carga. Passar do prazo aborta. |
| Intervalo de 10 min a 10 clientes | 2 min a 100 clientes | FULL e DIFF seguem em fases distintas; manter a carga é mais exigente que aliviá-la. |
| Recuperação de 15 min | 5 min | O gate exige filas na linha de base já no último minuto dos 5, mais cedo; não drenar a tempo é FAIL. |
| restic a cada repetição | ingestão inteira no ciclo 1, incremental nos ciclos 2 e 3 | É o padrão do restic diário da produção; o ciclo 1 é o caso pesado. |
| Restore com FULL novo depois | `restore-rehearsal --no-final-full` | O FULL depois do restore só refaz a cadeia de um sombra que é derrubado em seguida; RPO e RTO são medidos antes dele. |

Nada disso mexe em guardas, gates, limites de recurso, dataset ou carga por cliente. A saturação acima de 100 e o
reinício sob carga continuam fora (exigem host isolado).

Modelo por cliente: leitura a cada 10 s (`/me`, `/events`, participação, sala), comando a cada 5 s (inscrição e
check-in no evento sintético, aceite da sala que o worker forma, troca de fuso com `If-Match`, que grava e gera WAL).
Mensagens e SignalR não existem no produto: ficam como **pendentes** no relatório, sem stub.

### Guardas (abortam na hora)

| Código | Gatilho |
|---|---|
| `MEM_LOW` | MemAvailable < 512 MiB por 60 s |
| `OOM` | qualquer OOM kill no host ou em qualquer cgroup |
| `SWAP` | swap-in/out em todas as amostras de 60 s |
| `DISK` | filesystem ≥ 85% |
| `ARCHIVE_LAG` / `ARCHIVE_FAILED` | último WAL arquivado há ≥ 240 s, ou falha nova de archive |
| `BACKUP_ERROR` / `DEADLINE` | comando de backup com código ≠ 0, ou fase além do prazo |
| `WINDOW` | execução inteira além de `window_seconds` (80 min no perfil `janela`) |
| `FAILURE_RATE` | ≥ 5% de 5xx/timeout em 60 s (mínimo 20 requisições) |
| `OTHER_APPS_PSI` | PSI de CPU de qualquer container que não é do sombra > 40 por 60 s |
| `OTHER_APPS_HTTP` | 2 sondas HTTPS seguidas falhando num app externo, ou p95 das últimas 8 acima de max(3× a linha de base, base + 500 ms) |
| `PROBE_STALE` | 30 s sem amostra da VPS |

Ao abortar: para os clientes, encerra o comando de backup local, roda os `abort_commands` (no exemplo, `stop` do
projeto sombra e do restic) e grava o motivo. CPU > 85% por 5 min e throttling > 10% dos períodos viram
**investigação** no relatório, não aborto (capacity.md).

### Desvios do ambiente de produção (registrar no relatório)

- Sub-redes `10.231.10/20/21.0/24`: as fixas da produção já existem no host. O pg_hba e o `pgbackrest-repo.conf`
  da imagem entram por bind mount com o IP sombra; o resto da imagem é o mesmo.
- `RateLimiting__LoginPerIp__PermitLimit=100000`: todos os clientes saem do mesmo IP do túnel. O limite por conta
  continua o padrão.
- Repositório do pgBackRest no MinIO local e restic num repositório local: o upload ao B2 (rede) não é medido; CPU,
  memória e I/O de leitura/compressão/cifra são. O restic roda na slice transitória `duora-c2-media.slice`, com os
  mesmos limites da `duora-media.slice`, para não dividir o teto com o restic da produção.
- TLS interno do Caddy e carga pelo túnel SSH (o `sshd` da VPS entra na conta). A latência inclui o RTT do túnel;
  o relatório traz `rtt_floor_ms` (menor `GET /auth/csrf`) para separar aproximadamente app de rede.
- Dataset: 1.000 contas e 48 eventos sintéticos (um a cada 15 min). As 100.000 mensagens do protocolo não existem
  no produto.

## Roteiro da janela (perfil `janela`, até 3 h com a #174)

Pré-requisitos no Mac: `ssh c2vps` (alias para a VPS, root ou usuário com Docker e `sudo` sem senha) e o worktree
no commit do PR. Na VPS: Docker Compose ≥ 2.24.4 (`!override`), `restic`, `openssl`, Python 3.12, ≥ 15 GiB livres.
Escolher a janela fora dos timers de backup da produção (`systemctl list-timers 'duora*'`: hoje media/diff/
verify-repo/media-verify/media-check entre 02:35 e 06:45 UTC e o FULL de domingo às 03:06 UTC); o `verify` de
15 em 15 min fica ligado e entra na linha de base. OpenClaw pausado pelo responsável durante toda a janela.

| T (min) | Fase | Duração | Corte |
|---|---|---:|---|
| 0 | Pré-checagem e C1 antes | 5 | — |
| 5 | #174 (instalação e corpus, outro agente) | 45 | C2 não começa a carga depois de T+70 |
| 50 | Código, sombra (`prepare`, `up`, `media-fixture`) | 14 | `up` passou de 20 min: `down` e adiar o C2 |
| 64 | Config, túnel e fumaça | 4 | fumaça ≠ 204: `down` |
| 68 | Carga `janela` | 68 (máx. 80) | guardas e `WINDOW` |
| 136 | Restore do ensaio | 10 (máx. 20) | passou de 20 min: parar e registrar RTO inconclusivo |
| 146 | C1 depois, derrubar o sombra | 6 | — |
| 152 | Reativar OpenClaw e conferir apps | 3 | — |
| 155 | Folga | 25 | — |

Pior caso com todos os cortes: 70 + 80 + 20 + 6 + 3 = 179 min.

1. **C1 antes** (Mac): coletor somente leitura, como em [capacity.md](capacity.md#coletor-c1-somente-leitura),
   salvando `c2run/c1-before.json`.
2. **Copiar o código** (Mac):
   `git archive --format=tar HEAD | ssh c2vps 'sudo mkdir -p /opt/duora-c2/src && sudo tar -x -C /opt/duora-c2/src'`.
3. **Subir o sombra** (VPS):
   ```sh
   cd /opt/duora-c2/src
   ID=$(sudo python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["releaseId"])' /var/lib/duora/current.json)
   sudo python3 scripts/deploy/c2_shadow.py prepare --release-env /var/lib/duora/releases/$ID.env
   sudo python3 scripts/deploy/c2_shadow.py up --accounts 1000
   sudo python3 scripts/deploy/c2_shadow.py media-fixture --mib 2048
   ```
   Falhou em qualquer passo: `sudo python3 scripts/deploy/c2_shadow.py down` e reportar o erro; não improvisar.
4. **Config local** (Mac): `mkdir -p c2run && cp scripts/deploy/c2/c2.example.json c2run/c2.json`;
   `ssh c2vps 'sudo cat /var/lib/duora-c2/state/c2-root.crt' > c2run/c2-root.crt` e o mesmo para `accounts.json`
   (`chmod 600 c2run/accounts.json`); em `c2run/c2.json`, trocar as URLs `REPLACE-*` pelas URLs públicas de saúde de
   cada app da VPS (o harness recusa o marcador) e ajustar `probe_script` para `../scripts/deploy/c2_probe.py`.
5. **Túnel** (Mac, deixar aberto): `ssh -N -L 18443:127.0.0.1:18443 c2vps`. Fumaça:
   `curl --cacert c2run/c2-root.crt --resolve c2.duora.test:18443:127.0.0.1 -s -o /dev/null -w '%{http_code}' https://c2.duora.test:18443/api/v1/auth/csrf`
   deve responder 204.
6. **Carga** (Mac): `python3 scripts/deploy/c2_harness.py run --profile janela --config c2run/c2.json
   --repetition 1 --evidence c2run/evidence`. Código 3 é aborto (ler `events.jsonl`): não repetir na mesma janela,
   não reduzir guardas.
7. **Restore do ensaio** (VPS): `sudo python3 scripts/deploy/c2_shadow.py restore-rehearsal --no-final-full`
   (marcadores por 90 s, `docker kill` do postgres sombra, volume apagado, restore até o fim do WAL, web/worker de
   pé). No Mac: `python3 scripts/deploy/c2_harness.py restore-objectives` com os quatro instantes de
   `/var/lib/duora-c2/state/restore-rehearsal.json` (RPO ≤ 300 s, RTO ≤ 7.200 s).
8. **C1 depois** (Mac): `c2run/c1-after.json`, para comparar MemAvailable, containers e limites com o passo 1.
9. **Derrubar o sombra** (VPS): `sudo python3 scripts/deploy/c2_shadow.py down` (só volumes e diretório
   `duora-c2`) e `sudo rm -rf /opt/duora-c2`. Conferir `docker ps -a --filter label=com.docker.compose.project=duora-c2`
   vazio e os containers da produção intactos.
10. **Reativar o OpenClaw** e conferir os outros apps.

O perfil `completo` usa os mesmos passos com `--profile completo` e `--repetition 1`, `2` e `3` (162–310 min cada),
sem caber numa janela de 3 h.

Evidências por execução em `c2run/evidence/c2-<UTC>-rep<N>/`: `manifest.json` (commit, perfil, reduções, fases, limites, comandos,
hash do sampler), `events.jsonl` (fases, backups, guardas), `requests.jsonl`, `samples.jsonl` (brutos),
`health.jsonl`, `backup-*.log`, `report.json` (com `content_sha256`) e `report.md`. `accounts.json` e a raiz do
Caddy ficam fora da evidência e não vão para a issue.

Reportar na #86: commit, digests da release, `c1-before`/`c1-after` sanitizados, a tabela de cada `report.md`, os
`content_sha256`, guardas disparados, duração dos backups, RPO/RTO do ensaio, desvios acima e o que ficou pendente.
`PASS_CANDIDATE` (três ciclos no perfil `janela`, três repetições no `completo`) ainda não é aceite: exige revisão engenharia/segurança/operação.

## O que o harness não cobre

Mensagens/SignalR (não existem), saturação acima de 100 e reinício de processos durante carga (capacity.md exige
host isolado), upload real ao B2, corpus18 do sanitizador e a qualificação do Worker/launcher da #174.

## Ensaio da troca de digest do PostgreSQL

`c2_shadow.py swap-rehearsal` responde só duas perguntas: quanto dura, com volume de dados, parar e recriar o PostgreSQL no
digest alvo e voltar ao de origem, e se a imagem puxa do GHCR a partir da VPS. **Nada aqui é resultado medido.** Executar
só em janela autorizada pelo responsável, com `prepare` e `up` feitos como no roteiro acima.

```sh
cd /opt/duora-c2/src
sudo systemd-run --unit=duora-c2-swap --collect --service-type=exec --working-directory="$PWD" \
  --property=KillMode=mixed --property=TimeoutStopSec=900 \
  python3 scripts/deploy/c2_shadow.py swap-rehearsal \
  --target ghcr.io/bipoxdxd/duora-postgres@sha256:b5103bfd8ac7832915eed8170ffd0bcb94d9bca834cbd659114fa25606f0434e \
  --fill-mib 256
sudo journalctl --unit=duora-c2-swap --follow
sudo systemctl stop duora-c2-swap                       # interrompe e volta sozinho à origem

# só se o processo tiver morrido sem voltar
sudo systemd-run --unit=duora-c2-swap-abort --collect --service-type=exec --working-directory="$PWD" \
  --property=KillMode=mixed --property=TimeoutStopSec=900 \
  python3 scripts/deploy/c2_shadow.py swap-abort
sudo journalctl --unit=duora-c2-swap-abort --follow

sudo python3 scripts/deploy/c2_shadow.py down           # ao fim da janela, depois de copiar as evidências
```

O `systemd-run` desliga o ensaio e o abort da sessão SSH: uma queda do túnel não interrompe a troca nem a volta, e por isso o
abort também vai por ele. Sem ele, um terminal morto só perde o log, e o resultado segue nos JSON de `state/`. O digest alvo é o amd64 da
imagem construída na run 42073873377 do GitHub Actions. O `TimeoutStopSec` dá 15 min para a volta depois de um `stop`.

Em ordem: pull do alvo, dados sintéticos em `c2.swap_ballast`, espera do arquivamento, parada de worker e web, foto e
`pgbackrest check`. Depois recria com o alvo, confere digest, foto, volume e `check`, repete de volta para o digest de origem
e religa a aplicação. A foto junta `SWAP_PHOTO_SQL` e `SWAP_COMPATIBILITY_SQL`, as mesmas consultas do executor, com a contagem
e o md5 do lastro; ela aceita mudança de versão menor, mas não de major, catálogo, colação ou conteúdo. O resultado fica em
`/var/lib/duora-c2/state/swap-rehearsal.json`.

**O que cada tempo mede.** Nenhum deles é a indisponibilidade da produção.

| Campo | Mede | Não inclui |
| --- | --- | --- |
| `databaseDownSeconds` (em `forward` e `back`) | Do pedido de parada do postgres ao fim do `up --wait`, com a parada e a subida do backup e a espera do healthcheck | Parada da aplicação, conferências, `verify` e reabertura |
| `stopApplicationSeconds` | Parada de worker e web | |
| `reopenApplicationSeconds` | Subida de web, worker e edge até o healthcheck | |
| `applicationDownSeconds` | Da parada de web e worker à reabertura, com **ida e volta** e conferências | A espera e o `verify` da operação real |

Para estimar a queda de uma troca só, some `stopApplicationSeconds`, os tempos de `forward` e
`reopenApplicationSeconds`. O WAL do fast shutdown vai ao MinIO local, não ao B2, e o volume de buffers sujos depende do tempo
de arquivamento; um `stopPostgresSeconds` perto de 60 s sugere SIGKILL, e não parada limpa.

**Falhas.** Até a primeira parada, uma falha deixa o sombra de pé na origem, com `aborted` e o erro no JSON; o que ficou de
lastro é trocado pelo próximo preenchimento. Depois dela, um erro, `OSError`, SIGHUP, SIGTERM ou SIGINT volta ao digest de
origem no mesmo volume, com os sinais blindados durante a volta. O SIGKILL não volta: use `swap-abort`, que recria, confere
digest, foto (se houver `state/swap-baseline.json`) e `check`, só então religa a aplicação e registra tudo em
`state/swap-abort.json`. Se a conferência falhar, a aplicação fica parada de propósito. Repetir o ensaio fora da origem é
recusado com o pedido de `swap-abort`. Nunca há `down` nem remoção de volume no ensaio.

**Guardas, antes de qualquer efeito.** Só o digest alvo revisado no código, só com o sombra no digest de origem (`d32f8c44`),
projeto validado por `c2_probe.validate_project`, raiz com nome `duora-c2`. O `docker compose config` precisa resolver para o
projeto e o volume do sombra, com a imagem de origem em postgres e backup, todo bind mount e todo segredo dentro da raiz e toda
porta em loopback. Os containers precisam estar de fato na origem, conferidos pelo id da imagem. Os comandos rodam com ambiente
mínimo, sem herdar `DUORA_*`, `DOCKER_HOST`, `DOCKER_CONTEXT` nem `COMPOSE_*` do root, e um lock em `/var/lib/.duora-c2.lock`,
ao lado da raiz para o `down` não depender de `state/`, faz os subcomandos se excluírem. O comando não usa o executor, o receiver, `/etc/duora` nem `/var/lib/duora`.

**Guarda de recursos.** O início exige ao menos 1 GiB de `MemAvailable` e uso de disco abaixo de 85% contando a reserva, com o
`DockerRootDir` no mesmo filesystem da raiz. A mesma conta se repete entre lotes, a cada 10 s da espera do arquivamento e antes de
cada sentido, com piso de 512 MiB. Antes da primeira parada, ela para sem parar serviços. Depois, volta à origem. A volta em si
não passa pelo guarda, de propósito: parar no meio dela seria pior que terminar.

| Parada | Regra |
| --- | --- |
| Sonda `ready` | `docker exec duora-edge-1 wget --quiet --spider --timeout=5 http://duora-web:8080/health/ready`, a sonda do deploy. A linha de base é a mediana de 5 leituras no preflight, e falha nelas recusa. Para com 2 leituras ruins seguidas, sejam falha ou acima de `max(3 × base, base + 500 ms)` |
| CPU do host | Delta de `/proc/stat` desde a leitura anterior: para se idle + iowait ficar abaixo de 10% |
| Por container | `cpu.pressure` de todo container fora do sombra, em `/sys/fs/cgroup/system.slice/docker-<id>.scope`, só leitura, com teto 40 do `OTHER_APPS_PSI` |
| Containers do início | Todo container visto no preflight, mais web, worker e postgres, vira obrigatório. Sem ele, sem o cgroup ou com arquivo ilegível, para. `docker ps` tem 10 s de timeout |
| OOM | Qualquer aumento de `oom_kill` em `/proc/vmstat` |
| Memória e IO | PSI `some avg10` acima de 40 |

O sombra fica fora da leitura por container, porque o preenchimento o pressiona de propósito. O JSON registra `readyBaselineMs`,
`readyMaxMs`, `cpuIdleMinPercent`, `containerCpuPressureMax` e `hostCpuPressureMax`.

**PSI de CPU global não decide.** No ensaio de 06/11 às 16:13 UTC, ele foi a 71,79 por causa de cgroups do OpenClaw presos na
própria cota, com a CPU 91% ociosa, enquanto `duora-web-1` marcava no máximo 0,83 e o `ready` respondia em 10 ms. Medido localmente,
um único cgroup preso levou o global a 92,79 com o alvo em 0,00, então nenhum teto alto serve de rede: quem cumpre esse papel é o
idle de CPU, que também vale para o que não é container (sshd, receiver, gateway).

**O teto por container não basta.** A revisão adversarial mediu um alvo leve com p99 22 vezes maior, PSI do alvo em 22,54 e global
em 46,86, sem o guarda parar. A sonda `ready` entrou por isso. A medição abaixo foi feita antes dela, em Docker Desktop, kernel
`7.0.14-linuxkit`, 10 CPUs e cgroup v2, com a versão anterior do guarda lendo arquivos reais, por 40 s cada:

| Cenário | `cpu.pressure` do alvo | PSI de CPU global | Por container |
| --- | --- | --- | --- |
| Alvo com `--cpus 0.1` e 4 loops | 31,81 a 95,12 | 37,75 a 94,31 | Para aos 10 s, em 59,42 |
| Outro cgroup com `--cpus 0.1` e 4 loops, alvo leve | 0,00 | 61,86 a 92,79 | Não dispara |
| Outro cgroup sem cota com 30 loops, alvo leve | 0,00 a 0,35 | 57,47 a 83,41 | Não dispara |
| Outro cgroup sem cota com 30 loops, alvo com 4 loops | 2,18 a 9,68 | 45,47 a 65,79 | Não dispara |

**Limites do guarda.** A sonda é uma requisição por leitura e o piso é de 500 ms sobre a base: uma piora abaixo disso, ou que afete
só parte das requisições, não para o ensaio. Ela cobre só o `duora-web-1`. Durante cada sentido da troca não há leitura, e o
intervalo entre leituras no preenchimento é o tempo de um lote. Um container efêmero visto no preflight que termine depois para o
ensaio. As sondas externas dos apps vizinhos continuam com o operador. É uma medição de Docker Desktop, não da VPS de 2 vCPU.

O preenchimento roda com `--cpus 0.5` e `--cpu-shares 256`, com um intervalo de 1 s entre lotes, e devolve os limites definidos no
Compose ao terminar. O MinIO tem teto de 768 MiB e meia CPU no overlay.

**Dados.** O banco sombra nasce pequeno, e copiar a produção é proibido. `--fill-mib` grava dados sintéticos sem compressão
até 2048 MiB, em lotes de 64 MiB, reservando três vezes esse tamanho mais 4 GiB, e troca o lastro de um ensaio anterior. O
resultado traz o tamanho real em `databaseSizeMib`. O recomendado é `--fill-mib 256`: em 06/11 a produção tinha 117 MB em
`/var/lib/postgresql`.

**Reserva (estimativa, não medida).** Fora o `prepare` e o `up`, de 10 a 15 min: pull de 1 a 3, preenchimento de 256 MiB de 1 a
2, arquivamento até 10 no pior caso, cada sentido de 1 a 2, e `down`. Reserve 45 min. Com a CPU reduzida, o preenchimento pode
passar disso. Para fazer também o `restore-rehearsal` na mesma janela, rode um `full` antes dele: o ensaio da troca não grava
backup.

**Memória e disco.** Os limites do sombra somam 5,1 GiB (postgres 2 GiB, web 1,5, worker 1, backup 0,5, edge 0,1), mais 0,75 do
MinIO. Worker e web ficam parados durante a troca, com uso esperado de 2 a 3 GiB, mas o pico é no preenchimento, com tudo de pé.
Disco: 4,8 GiB reservados para `--fill-mib 256`, com a imagem alvo, de que boa parte das camadas já está no host. Vale a regra de um comando
pesado por vez da janela. Os demais guardas da janela C2 (por exemplo os alertas externos) continuam sendo acompanhados pelo
responsável: este comando só aplica os de memória, disco e PSI listados acima.

**O que não prova.** O protocolo do executor (journal, fence, commit, ressela e `--resume`), que o laboratório cobre; o
receiver, Coolify e observer; o B2, porque o repositório é o MinIO local; a aplicação rodando no digest alvo, já que web e
worker ficam parados entre os dois sentidos; o `docker load` do receiver sobre a imagem puxada; o cron de prune. O dado é
sintético e sem carga: a parada mede o checkpoint desse volume, não o da produção sob tráfego. O PITR atravessando a troca
também não, que é do `restore_drill.py`.
