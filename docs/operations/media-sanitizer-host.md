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

## Runbook

Pré-requisitos: worktree do repositório com este role, `uv` no Mac (o Ansible roda por `uvx`, sem instalar nada na
VPS) e a mesma chave/known_hosts do acesso root. O inventário fica fora do git.

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
```

### 1. Pré-checagens (leitura; ~2 min; risco nulo)

```sh
ssh root@VPS 'set -e; free -m; df -h /; docker ps -q | wc -l; cat /etc/subuid /etc/subgid;
  ! id duora-media-launcher; ! test -e /var/lib/duora-media-launcher; ! test -e /var/lib/duora-media-runtime;
  sysctl kernel.apparmor_restrict_unprivileged_userns; grep -c /usr/bin/rootlesskit /etc/apparmor.d/rootlesskit;
  stat -c "%U:%G %a %n" /var/lib/duora-media /var/lib/duora-media/*; docker version --format "{{.Server.Version}}"'
```

Esperado: nenhuma linha `duora-media-launcher` em subuid/subgid e nada em `231072–296607`; sysctl `= 1`; fotos com
`root:root 755` e `1654:1654 700`. Guarde a contagem de containers e as permissões das fotos para comparar.

### 2. Instalar inativo (~3–5 min; risco baixo)

```sh
$AP --diff        # instala; o resumo final mostra daemon parado e launcher static/inactive
$AP               # reaplicar: changed=0 prova a idempotência
```

Risco: `apt` instala quatro pacotes pequenos; o gerenciador de usuário da conta nova sobe (linger) sem processos
de container. `--check` só é útil a partir da segunda aplicação (a primeira depende da conta já existir).

### 3. Verificar (leitura; ~1 min; risco nulo)

```sh
ssh root@VPS 'U=$(id -u duora-media-launcher); grep duora-media-launcher /etc/subuid /etc/subgid;
  cat /sys/fs/cgroup/user.slice/user-$U.slice/{memory.max,memory.swap.max,cpu.max,pids.max};
  systemctl is-enabled duora-media-launcher.service; systemctl is-active duora-media-launcher.service || true;
  systemctl --machine=duora-media-launcher@.host --user is-active docker.service || true;
  stat -c "%U:%G %a %n" /var/lib/duora-media /var/lib/duora-media/*; docker ps -q | wc -l'
```

Esperado: `231072:65536` nos dois arquivos; `2147483648`, `0`, `150000 100000`, `1024`; `static`/`inactive`;
daemon `inactive`; fotos e contagem de containers iguais às do passo 1.

Opcional, fixar o perfil com a imagem por digest (não inicia nada; imprime `profile_sha256` do perfil **deste**
host): `$AP -e media_sanitizer_image=ghcr.io/bipoxdxd/duora-media-sanitizer@sha256:<digest amd64>`.

### 4. Rodar o corpus com coleta (~10–20 min; risco baixo a médio)

No Mac, baixe o kit compilado da main e confira o hash (o ZIP é verificado de novo no host contra o próprio
manifesto fechado, membro a membro):

```sh
gh api repos/BipoXDXD/duora/actions/artifacts/<id media-host-instrumentation-amd64-1>/zip > kit.zip
shasum -a 256 kit.zip
scp kit.zip root@VPS:/var/tmp/duora-174-kit.zip
```

Na VPS (o `$AP` com `runtime_started=true` só inicia o daemon dedicado):

```sh
$AP -e media_sanitizer_runtime_started=true
ssh root@VPS 'install -d -m 0700 /var/lib/duora-174-evidence &&
  python3 -I /opt/duora/media/host_corpus.py --kit-zip /var/tmp/duora-174-kit.zip --kit-sha256 <sha256 do ZIP> \
    --evidence-dir /var/lib/duora-174-evidence/$(date -u +%Y%m%dT%H%M%SZ)'
```

O que roda, tudo no daemon rootless dedicado e com dados sintéticos:

1. Preflight: socket 0600 do próprio UID, `rootless` + `seccomp builtin` + cgroup v2/systemd + `overlay2`, daemon
   sem containers; registra os tetos do slice e os IDs dos containers do daemon do sistema.
2. Pull do SDK fixado por digest (o mesmo do workflow "Media isolation"; ~1 GB na primeira vez).
3. Sonda com **os argumentos de criação do próprio launcher** (só o entrypoint muda): lê `memory.max`,
   `memory.swap.max`, `cpu.max`, `pids.max`, Seccomp/NoNewPrivs/CapEff e UID dentro do cgroup do container;
   prova só `lo`, DNS e TCP falhando, rootfs somente leitura, sem `/run/secrets`, socket Docker, fotos ou `/etc/duora`.
4. Corpus: kit somente leitura, `--network none`, 1 GiB/0,5 CPU/256 PIDs (iguais ao CI). Do lado do host lê o
   cgroup real do processo e de todos os ancestrais até o slice, `mountinfo`, interfaces e **nomes** das variáveis de
   ambiente; exige 18 casos únicos, 2 amostras cada e o resumo do xUnit sem falha, erro, skip ou not-run.
5. Falhas contidas: alocação acima de 512 MiB morta pelo kernel (exit 137), SIGKILL, prazo externo de 5 s com
   remoção confirmada e reinício do daemon dedicado sem containers sobrando.
6. Limpeza só dos containers criados pela execução; confere que os containers do daemon do sistema são os mesmos.

Risco: CPU limitada a 1,5 núcleo e memória a 2 GiB pelo slice; um OOM proposital acontece dentro de um container de
512 MiB. Se a memória livre do host estiver baixa, pause o OpenClaw antes (autorizado pelo dono) e retome depois.
Interromper (Ctrl-C) remove os containers da execução no `finally`; nenhum recurso de outra conta é tocado.

### 5. Coletar evidências (~2 min; risco nulo)

```sh
ssh root@VPS 'cd /var/lib/duora-174-evidence/<execução> && sha256sum -c SHA256SUMS && python3 -m json.tool evidence.json'
scp -r root@VPS:/var/lib/duora-174-evidence/<execução> .
```

`evidence.json` traz `passed`, `failures` e, por verificação: `sandboxLimitsAndIsolation.values` (limites lidos do
cgroup do sandbox), `corpus.observed.cgroupChain` (cliente e ancestrais), `corpus.observed.interfaces`,
`corpus.mountPoints`, `corpus.cases`/`summary`, `oomKill`, `sigkill`, `externalDeadline`, `daemonRestart`, `cleanup`
e `systemDaemonPreserved`. `corpus.log` e `probe.txt` são saídas sintéticas; nenhum segredo é lido ou gravado.
`operationalAcceptance` é sempre `false`: o aceite é do dono (#174) e do responsável #86.

### 6. Voltar a inativo (~1 min; risco nulo)

```sh
$AP               # runtime_started=false: para o daemon dedicado
ssh root@VPS 'rm -f /var/tmp/duora-174-kit.zip'
```

### 7. Desfazer (~2 min; risco baixo)

```sh
$AP -e media_sanitizer_state=absent
ssh root@VPS '! id duora-media-launcher; ! grep duora-media-launcher /etc/subuid /etc/subgid; ls /etc/systemd/system | grep -c duora-media || true'
```

Para o daemon e o launcher, desliga o linger, remove drop-ins, unidade, código, perfil, autoridade, conta (com o home
e o cache sintético do daemon), faixa subordinada e grupo. Mantém os pacotes e as evidências.

## O que continua pendente

- Aceite de P174-3/4 e do teto do slice (2 GiB/1,5 CPU) no orçamento integral da #86.
- Nova publicação licenciada a partir de uma main com este role: o bundle precisa carregar o launcher com a nova
  autoridade e o perfil deste host com pin próprio.
- Ativação (Worker no grupo de controle, socket montado, launcher iniciado), carga com backup concorrente,
  observação independente, recuperação/rollback/restore e aceite operacional.
