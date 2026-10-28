# Runtime rootless do media-sanitizer no host (#174)

Instalação idempotente e **inativa** do isolamento do ADR-0036 na VPS atual e ensaio do corpus sintético de 18
casos com coleta de evidências. Complementa [media-sanitizer-release.md](media-sanitizer-release.md) e o roteiro
[media-host-qualification.md](../testing/reports/media-host-qualification.md). Nada aqui ativa upload, conecta o
Worker ao launcher, altera limites de produto ou aprova P174-3/4, que continuam candidatos.

## O que o role `media_sanitizer` instala

Playbook próprio, `infra/ansible/media-sanitizer.yml`: a VPS atual não é gerida pelo `site.yml` (ssh/firewall/docker
de lá não se aplicam), então só este role roda.

| Item | Valor | Por quê |
|---|---|---|
| Pacotes | `uidmap` (newuidmap/newgidmap), `slirp4netns`, `dbus-user-session`, `docker-ce-rootless-extras` | Pré-requisitos do Docker rootless |
| Conta | `duora-media-launcher` (sistema, sem login), home `/var/lib/duora-media-runtime` 0700 | Dona do daemon rootless **e** do launcher: o perfil exige `/run/user/<uid>/docker.sock` do próprio UID |
| Grupo | `duora-media-client` | Grupo do socket de controle; o Worker só entra nele na ativação |
| subuid/subgid | `231072:65536` | Primeira faixa livre depois de `goblinshift` e `deploy`; o role recusa sobreposição |
| AppArmor | Exige `/etc/apparmor.d/rootlesskit` (Ubuntu) | `kernel.apparmor_restrict_unprivileged_userns` continua `1`; nada global é enfraquecido |
| cgroup | `user-<uid>.slice`: `MemoryMax=2G`, `MemorySwapMax=0`, `CPUQuota=150%`, `TasksMax=1024`; delegação `cpu memory pids` só em `user@<uid>.service` | Teto de toda a conta (daemon + containers); o role lê os valores do kernel e falha se divergirem |
| Daemon | Unidade de usuário `docker.service` sem `[Install]`, `overlay2` clássico, socket 0600 | Parado fora do ensaio; o launcher recusa socket acessível a grupo |
| Launcher | Código em `/opt/duora/media`, unidade `duora-media-launcher.service` (sem `[Install]`), autoridade `/var/lib/duora-media-launcher` 0700 | Instalado **static/inactive**; o role falha se estiver habilitado ou ativo |
| Perfil | `/etc/duora-media-launcher.json`, só com `media_sanitizer_image` por digest | Sem imagem fixada não há perfil, e o launcher não sobe |

A autoridade do launcher saiu de `/var/lib/duora-media`, que no host é a raiz das fotos (quarentena e derivados,
dono 1654): o `StateDirectory=` do systemd faria `chown` recursivo das fotos para a conta do launcher. O perfil saiu
de `/etc/duora/`, que é `root:root 0750` e a conta não atravessa. Os dois caminhos fazem parte do bundle da release;
o bundle `ba5` publicado antes desta mudança não serve para ativar o launcher neste host.

Não toca: `/etc/docker`, o daemon do sistema e seus containers, `/var/lib/duora-media`, `/etc/duora`, segredos,
Caddy/Coolify e as demais contas.

## Runbook (janela de 45 min)

A janela total de testes na VPS (#174 + C2 da #86) é de no máximo 2 a 3 horas; a #174 tem **45 min** reservados.
Tudo que não toca a VPS acontece antes da janela. Cada passo tem teto: estourou, siga para "Abortar" em vez de
esticar a janela.

| Passo | Teto | Risco | Mexe na VPS? |
|---|---|---|---|
| 0. Preparar no Mac | antes da janela | nulo | não |
| 1. Pré-checagens | 3 min | nulo | leitura |
| 2. Instalar inativo + idempotência | 6 min | baixo | sim |
| 3. Verificar limites, rede e segredos | 2 min | nulo | leitura |
| 4. Corpus com coleta | 22 min (corte automático aos 20) | baixo a médio | sim, só a conta dedicada |
| 5. Coletar evidências | 3 min | nulo | leitura |
| 6. Voltar a inativo | 2 min | nulo | sim |
| Folga (inclui o passo 7, se preciso) | 7 min | | |

Abortar (qualquer passo): `Ctrl-C` (no passo 4 o `finally` remove só os containers da execução e grava a evidência
parcial), depois o passo 6. Se a instalação ficou pela metade ou algo divergiu do esperado, o passo 7 desfaz.

### 0. Preparar no Mac (antes da janela; risco nulo)

Pré-requisitos: worktree com este role, `uv` e a mesma chave/known_hosts do acesso root. O inventário fica fora do
git. Ansible e kit ficam prontos aqui para não gastar a janela com download.

```sh
cat > "$TMPDIR/duora-174-inventory.yml" <<'EOF'
all:
  children:
    duora_vps:
      hosts:
        vps-atual:
          ansible_host: <IP da VPS>
          ansible_user: root
          ansible_ssh_private_key_file: <chave root da VPS>
          ansible_ssh_common_args: -o IdentitiesOnly=yes -o UserKnownHostsFile=<known_hosts da VPS>
EOF
cd infra/ansible
AP="uvx --from ansible-core==2.21.5 ansible-playbook -i $TMPDIR/duora-174-inventory.yml media-sanitizer.yml"
$AP --syntax-check                       # aquece o cache do uvx; não conecta na VPS
# Kit compilado no MESMO commit do role (job backend do CI deste head):
gh api "repos/BipoXDXD/duora/actions/artifacts?name=media-host-instrumentation-amd64-1" \
  --jq '.artifacts[] | [.id, .workflow_run.head_sha[0:8], .expired] | @tsv' | head -3
gh api repos/BipoXDXD/duora/actions/artifacts/<id>/zip > "$TMPDIR/duora-174-kit.zip"
shasum -a 256 "$TMPDIR/duora-174-kit.zip"   # anote o hash: vai em --kit-sha256
```

### 1. Pré-checagens (3 min; leitura; risco nulo)

```sh
ssh root@VPS 'set -e; nproc; grep MemAvailable /proc/meminfo; df -h /; uptime; docker ps -q | wc -l;
  cat /etc/subuid /etc/subgid; ! id duora-media-launcher; ! test -e /var/lib/duora-media-launcher;
  ! test -e /var/lib/duora-media-runtime; sysctl kernel.apparmor_restrict_unprivileged_userns;
  grep -c "profile rootlesskit /usr/bin/rootlesskit" /etc/apparmor.d/rootlesskit;
  stat -c "%u:%g %a %n" /var/lib/duora-media /var/lib/duora-media/*; docker version --format "{{.Server.Version}}";
  command -v tmux'
```

Esperado: nenhuma linha `duora-media-launcher` em subuid/subgid e nada em `231072–296607`; sysctl `= 1`; grep `1`;
fotos `0:0 755` na raiz e `1654:1654 700` em `quarantine`/`derivatives` (numérico: o UID 1654 não tem nome no
host). Guarde a contagem de containers e as permissões para comparar.

**Não siga** se `MemAvailable` estiver abaixo de 2,5 GiB (o slice pode usar 2 GiB e o host não tem swap) ou se o
load passar do número de núcleos: pause o OpenClaw (autorizado pelo dono) e confira de novo, ou adie.

### 2. Instalar inativo (6 min; risco baixo)

```sh
$AP --diff        # instala; o resumo final mostra daemon parado e launcher static/inactive
$AP               # reaplicar: changed=0 prova a idempotência
```

Risco: `apt` instala `uidmap` e `slirp4netns` (os outros dois já estão no host); o gerenciador de usuário da conta
nova sobe (linger) sem processos de container. Falhou, ou `changed` diferente de 0 na segunda: passo 7.

### 3. Verificar limites, rede e segredos (2 min; leitura; risco nulo)

```sh
ssh root@VPS 'U=$(id -u duora-media-launcher); grep duora-media-launcher /etc/subuid /etc/subgid;
  cat /sys/fs/cgroup/user.slice/user-$U.slice/{memory.max,memory.swap.max,cpu.max,pids.max};
  systemctl is-enabled duora-media-launcher.service; systemctl is-active duora-media-launcher.service || true;
  systemctl --machine=duora-media-launcher@.host --user is-active docker.service || true;
  stat -c "%u:%g %a %n" /var/lib/duora-media /var/lib/duora-media/*; docker ps -q | wc -l'
```

Esperado: `231072:65536` nos dois arquivos; `2147483648`, `0`, `150000 100000`, `1024`; `static`/`inactive`;
daemon `inactive`; fotos e contagem de containers iguais às do passo 1. Rede e segredos dentro do sandbox são
provados pela sonda do passo 4 (só `lo`, DNS/TCP falhando, sem `/run/secrets`, socket Docker, fotos ou `/etc/duora`).

Fixar o perfil com a imagem por digest fica **fora** desta janela: não muda o ensaio e só serve à ativação.

### 4. Rodar o corpus com coleta (22 min; risco baixo a médio)

```sh
scp "$TMPDIR/duora-174-kit.zip" root@VPS:/var/tmp/duora-174-kit.zip
$AP -e media_sanitizer_runtime_started=true      # só inicia o daemon dedicado (~1 min)
ssh -t root@VPS tmux new -s duora174             # sobrevive a queda do SSH; reentrar: tmux attach -t duora174
```

Dentro do `tmux` na VPS (o `timeout` manda SIGINT aos 20 min, o que dispara a limpeza do `finally`):

```sh
install -d -m 0700 /var/lib/duora-174-evidence
RUN=/var/lib/duora-174-evidence/$(date -u +%Y%m%dT%H%M%SZ)
timeout -s INT -k 120 20m python3 -I /opt/duora/media/host_corpus.py --kit-zip /var/tmp/duora-174-kit.zip \
  --kit-sha256 <sha256 do ZIP> --evidence-dir "$RUN" 2>&1 | tee /var/tmp/duora-174-run.log; echo "$RUN"
```

Duração esperada: pull do SDK (~1 GB, só na primeira vez) 2–5 min, corpus 3–6 min com 0,5 CPU, falhas contidas
2–3 min. Para acompanhar o host em outro terminal: `ssh root@VPS 'uptime; grep MemAvailable /proc/meminfo'`.

O que roda, tudo no daemon rootless dedicado e com dados sintéticos:

1. Preflight: socket 0600 do próprio UID, `rootless` + `seccomp builtin` + cgroup v2/systemd + `overlay2`, daemon
   sem containers; registra os tetos do slice e os IDs dos containers do daemon do sistema.
2. Pull do SDK fixado por digest (o mesmo do workflow "Media isolation").
3. Sonda com **os argumentos de criação do próprio launcher** (só o entrypoint muda): lê `memory.max`,
   `memory.swap.max`, `cpu.max`, `pids.max`, Seccomp/NoNewPrivs/CapEff e UID dentro do cgroup do container;
   prova só `lo`, DNS e TCP falhando, rootfs somente leitura, sem `/run/secrets`, socket Docker, fotos ou `/etc/duora`.
4. Corpus: kit somente leitura, `--network none`, 1 GiB/0,5 CPU/256 PIDs (iguais ao CI). Do lado do host lê o
   cgroup real do processo e de todos os ancestrais até o slice, `mountinfo`, interfaces e **nomes** das variáveis de
   ambiente; exige 18 casos únicos, 2 amostras cada e o resumo do xUnit sem falha, erro, skip ou not-run.
5. Falhas contidas: alocação acima de 512 MiB morta pelo kernel (exit 137), SIGKILL, prazo externo de 5 s com
   remoção confirmada e reinício do daemon dedicado sem containers sobrando.
6. Limpeza só dos containers criados pela execução; confere que os containers do daemon do sistema são os mesmos.

Risco: o slice limita a conta a 1,5 dos 2 núcleos e a 2 GiB; durante o pull e o corpus a produção fica com cerca de
0,5 núcleo livre, e a latência pode subir por alguns minutos. O OOM proposital acontece dentro de um container de
512 MiB. Nenhum recurso de outra conta é tocado. Não use `--skip-failures`: as falhas contidas fazem parte do ensaio.
Corte aos 20 min ou `passed: false`: registre como execução incompleta ou reprovada; não repita dentro da janela.

### 5. Coletar evidências (3 min; risco nulo)

```sh
ssh root@VPS 'cd <RUN> && sha256sum -c SHA256SUMS && python3 -m json.tool evidence.json | head -80'
scp -r root@VPS:<RUN> . && scp root@VPS:/var/tmp/duora-174-run.log .
```

`evidence.json` traz `passed`, `failures` e, por verificação: `sandboxLimitsAndIsolation.values` (limites lidos do
cgroup do sandbox), `corpus.observed.cgroupChain` (cliente e ancestrais), `corpus.observed.interfaces`,
`corpus.mountPoints`, `corpus.cases`/`summary`, `oomKill`, `sigkill`, `externalDeadline`, `daemonRestart`, `cleanup`
e `systemDaemonPreserved`. `corpus.log` e `probe.txt` são saídas sintéticas; nenhum segredo é lido ou gravado.
`operationalAcceptance` é sempre `false`: o aceite é do dono (#174) e do responsável #86.

### 6. Voltar a inativo (2 min; risco nulo)

```sh
$AP               # runtime_started=false: para o daemon dedicado; resumo com daemon parado e launcher static/inactive
ssh root@VPS 'rm -f /var/tmp/duora-174-kit.zip /var/tmp/duora-174-run.log; tmux kill-session -t duora174 || true;
  docker ps -q | wc -l'
```

A contagem de containers volta à do passo 1, e a C2 da #86 pode começar.

### 7. Desfazer (3 min, dentro da folga; risco baixo)

```sh
$AP -e media_sanitizer_state=absent
ssh root@VPS '! id duora-media-launcher; ! grep duora-media-launcher /etc/subuid /etc/subgid;
  ls /etc/systemd/system | grep -c duora-media || true'
```

Para o daemon e o launcher, desliga o linger, remove drop-ins, unidade, código, perfil, autoridade, conta (com o home
e o cache do SDK, que exigirá novo pull), faixa subordinada e grupo. Mantém os pacotes e as evidências.

## O que continua pendente

- Aceite de P174-3/4 e do teto do slice (2 GiB/1,5 CPU) no orçamento integral da #86.
- Nova publicação licenciada a partir de uma main com este role: o bundle precisa carregar o launcher com a nova
  autoridade e o perfil deste host com pin próprio.
- Ativação (Worker no grupo de controle, socket montado, launcher iniciado), carga com backup concorrente,
  observação independente, recuperação/rollback/restore e aceite operacional.
