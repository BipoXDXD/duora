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

Fases de uma repetição (`python3 scripts/deploy/c2_harness.py plan`): linha de base sem carga (2 min), aquecimento
(10 clientes, 5 min), rampa 10/25/50/100 (5 min cada), pico 100 por 30 min, entrada (100 logins em 60 s),
3 reconexões de 50% em 10 s, recuperação (10 clientes, 15 min), **FULL** com 100 clientes (mínimo 30 min e até o
backup terminar), intervalo de 10 min, **DIFF** + restic com 100 clientes (mesma regra) e recuperação final de
15 min. Nominal 162 min, prazo máximo 310 min por repetição; o protocolo pede 3 repetições. Fases não são truncadas:
fase de backup que passa do prazo aborta.

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

## Roteiro da janela

Pré-requisitos no Mac: `ssh c2vps` (alias para a VPS, como root ou usuário com Docker e `sudo` sem senha: o sampler
lê `docker ps`) sem senha; worktree no commit do PR. Na VPS: Docker Compose
≥ 2.24.4 (`!override`), `restic`, `openssl`, Python 3.12, ≥ 15 GiB livres e nenhum backup da produção agendado na
janela (`systemctl list-timers 'duora*'`). Pausar o OpenClaw antes do passo 3 e reativar no passo 10.

1. **C1 antes** (Mac, ~2 min): coletor somente leitura, como em [capacity.md](capacity.md#coletor-c1-somente-leitura),
   salvando `c1-before.json`.
2. **Copiar o código** (Mac, ~1 min):
   `git archive --format=tar HEAD | ssh c2vps 'sudo mkdir -p /opt/duora-c2/src && sudo tar -x -C /opt/duora-c2/src'`.
3. **Subir o sombra** (VPS, 15–25 min, baixa a imagem do MinIO fixada por digest):
   ```sh
   cd /opt/duora-c2/src
   ID=$(sudo python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["releaseId"])' /var/lib/duora/current.json)
   sudo python3 scripts/deploy/c2_shadow.py prepare --release-env /var/lib/duora/releases/$ID.env
   sudo python3 scripts/deploy/c2_shadow.py up --accounts 1000
   sudo python3 scripts/deploy/c2_shadow.py media-fixture --mib 2048
   ```
   Falhou em qualquer passo: `sudo python3 scripts/deploy/c2_shadow.py down` e reportar o erro; não improvisar.
4. **Config local** (Mac, ~3 min): `mkdir -p c2run && cp scripts/deploy/c2/c2.example.json c2run/c2.json`;
   `ssh c2vps 'sudo cat /var/lib/duora-c2/state/c2-root.crt' > c2run/c2-root.crt` e o mesmo para `accounts.json`
   (`chmod 600 c2run/accounts.json`); em `c2run/c2.json`, trocar as URLs `REPLACE-*` pelas URLs públicas de saúde de
   cada app da VPS (o harness recusa o marcador) e ajustar `probe_script` para `../scripts/deploy/c2_probe.py`.
5. **Túnel** (Mac, deixar aberto): `ssh -N -L 18443:127.0.0.1:18443 c2vps`. Fumaça:
   `curl --cacert c2run/c2-root.crt --resolve c2.duora.test:18443:127.0.0.1 -sI https://c2.duora.test:18443/api/v1/auth/csrf`
   deve responder 204.
6. **Repetições** (Mac, 162–310 min cada): `python3 scripts/deploy/c2_harness.py run --config c2run/c2.json
   --repetition 1 --evidence c2run/evidence` e depois `--repetition 2` e `3`. Entre repetições, conferir
   `report.md`; código 3 é aborto (ler `events.jsonl`, corrigir a causa, não reduzir guardas). As 3 podem ficar em
   janelas diferentes, com o sombra parado (`docker compose ... stop`) entre elas.
7. **Restore do ensaio** (VPS, 10–30 min): `sudo python3 scripts/deploy/c2_shadow.py restore-rehearsal` (marcadores
   por 90 s, `docker kill` do postgres sombra, volume apagado, restore até o fim do WAL, web/worker de pé, FULL novo).
   No Mac: `python3 scripts/deploy/c2_harness.py restore-objectives` com os quatro instantes de
   `/var/lib/duora-c2/state/restore-rehearsal.json` (RPO ≤ 300 s, RTO ≤ 7.200 s).
8. **C1 depois** (Mac, ~2 min): `c1-after.json`, para comparar MemAvailable, containers e limites com o passo 1.
9. **Derrubar o sombra** (VPS, ~2 min): `sudo python3 scripts/deploy/c2_shadow.py down` (só volumes e diretório
   `duora-c2`) e `sudo rm -rf /opt/duora-c2`. Conferir `docker ps -a --filter label=com.docker.compose.project=duora-c2`
   vazio e os containers da produção intactos.
10. **Reativar o OpenClaw** e conferir os outros apps.

Evidências por repetição em `c2run/evidence/c2-<UTC>-rep<N>/`: `manifest.json` (commit, fases, limites, comandos,
hash do sampler), `events.jsonl` (fases, backups, guardas), `requests.jsonl`, `samples.jsonl` (brutos),
`health.jsonl`, `backup-*.log`, `report.json` (com `content_sha256`) e `report.md`. `accounts.json` e a raiz do
Caddy ficam fora da evidência e não vão para a issue.

Reportar na #86: commit, digests da release, `c1-before`/`c1-after` sanitizados, a tabela de cada `report.md`, os
`content_sha256`, guardas disparados, duração dos backups, RPO/RTO do ensaio, desvios acima e o que ficou pendente.
`PASS_CANDIDATE` em três repetições ainda não é aceite: exige revisão engenharia/segurança/operação.

## O que o harness não cobre

Mensagens/SignalR (não existem), saturação acima de 100 e reinício de processos durante carga (capacity.md exige
host isolado), upload real ao B2, corpus18 do sanitizador e a qualificação do Worker/launcher da #174.
