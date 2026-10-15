# Backup e restore do PostgreSQL

Backup físico com pgBackRest 2.59.3, arquivamento contínuo de WAL e restore PITR do Duora (spec 18, 12.4, 15.4 e
19.1). Deploy e rede: [deploy.md](deploy.md). Infraestrutura: [infra/README.md](../../infra/README.md).

**Metas (spec 18.1, propostas, não SLA):** RPO de até 5 min e RTO de até 2 h para serviço seguro restaurado. O
drill local mede os dois em cada execução (abaixo); a prova com o volume real depende da VPS.

## Arquitetura

```
             rede data (internal)            rede pgbackrest (internal, 172.30.21.0/24)     rede backup-egress
web, worker ─────────────── postgres ──── WAL por TLS (8432, cert. de cliente) ──▶ backup ── HTTPS ──▶ repositório S3
migrator                    │ archive_command  ◀── SQL como duora_backup (scram) ── │ 172.30.21.10      (externo)
                            │ archive-push                                         │ pgbackrest server
                            └── volume pgdata ──────── só leitura ─────────────────┘ credenciais S3 + cifra
```

| Container | Redes | Tem | Não tem |
|---|---|---|---|
| `postgres` | `data`, `pgbackrest` (ambas `internal`) | `archive_command` do pgBackRest, certificado de **cliente** TLS, `pg_hba` sem `trust` | saída para fora, porta publicada, credencial do S3, senha de cifra |
| `backup` (repository host) | `pgbackrest` (IP fixo `172.30.21.10`), `backup-egress` | `pgbackrest server` (TLS só no IP da rede `pgbackrest`, exige certificado de cliente `duora-postgres`), credenciais do S3, senha de cifra, PGDATA **só leitura**, senha do papel `duora_backup` | socket do postgres, superusuário, porta publicada, `privileged`, capabilities (`cap_drop: ALL`, `read_only`) |
| `restore` (job, profile `restore`) | `backup-egress` | credenciais do S3 e cifra, PGDATA **gravável** | servidor TLS, acesso ao banco; só existe durante um restore |
| `web`, `worker`, `migrator` | `data` (e `edge`/`egress`) | nada do backup | credencial do repositório, socket do Docker, acesso a arquivo ou programa do servidor pelo SQL (teste `test_app_cannot_read_server_files_or_run_programs`) |

### Decisão: repository host via TLS (decidido pelo usuário em 2026-10-15)

A primeira versão deste PR punha o postgres numa rede com saída para o S3. A revisão de segurança apontou
`network-egress-control-missing`: um banco com saída para a internet facilita exfiltração se o processo for
comprometido (spec 15.2). Opções avaliadas:

| Opção | Prós | Contras |
|---|---|---|
| **(a) Repository host via `pgbackrest server` (escolhida)** | O postgres fica só em redes `internal`, sem nenhuma rota para fora; credenciais do S3 e senha de cifra saem do processo do banco (um postgres comprometido não lê nem apaga o backup); o filtro de saída só precisa cuidar de um container sem SQL exposto | Um container a mais; CA e certificados próprios para gerar, guardar e rotacionar (825 dias); o restore usa um job separado |
| (b) Archive no postgres + filtro `DOCKER-USER` por IP do endpoint | Topologia mais simples | O banco continua com saída (mesmo filtrada) e com credencial que apaga o repositório; endpoint S3 resolve para IPs que mudam (CDN), então a allowlist por IP é frágil |

Variante adotada dentro de (a): o `backup` lê o PGDATA **só leitura** e faz os backups localmente, em vez de um
terceiro container agente pgBackRest (`pg1-host`) junto ao postgres. No modo repository host clássico o repositório
não lê o PGDATA, mas o agente do lado do banco lê (o backup físico é a cópia desses arquivos): o acesso aos dados só
muda de container, e o agente traria mais um par de certificados. Desta forma, o `backup` não recebe o socket do
postgres. O SQL de controle do backup (`pg_backup_start/stop`, `pg_switch_wal`, `pg_create_restore_point`) vai por
TCP com um papel próprio e sem superusuário (abaixo). A mesma imagem (`duora-postgres`) roda os dois lados, porque o
pgBackRest exige a mesma versão no cliente e no servidor.

### Autenticação no banco e no servidor TLS

O `pg_hba` ativo é `infra/images/postgres/pg_hba.duora.conf` (via `hba_file`), **sem nenhuma entrada `trust`**.
Nem o `pg_hba.conf` gerado pelo initdb tem `trust` (`POSTGRES_INITDB_ARGS=--auth-local=peer --auth-host=scram-sha-256`).

| Quem | Como | Onde |
|---|---|---|
| `postgres` (superusuário) | `peer` | só pelo socket, dentro do próprio container; pela rede é sempre recusado |
| `duora_backup` | `scram-sha-256` | só `172.30.21.10/32` (o `backup`) e só no banco `postgres`; recusado no socket e em qualquer outra origem |
| `duora_app`, `duora_migrator` | `scram-sha-256` | rede (`data`) |
| qualquer outro | `reject` | — |

O papel `duora_backup` (`initdb/10-duora-roles.sh`) não é superusuário, não cria papel nem banco, não tem
`REPLICATION` nem `CONNECT` no banco `duora`. Ele tem só `pg_read_all_settings` (o pgBackRest confere
`data_directory` e o arquivamento) e `EXECUTE` em `pg_backup_start`, `pg_backup_stop`, `pg_switch_wal` e
`pg_create_restore_point`. É o mínimo com que o drill roda `stanza-create`, `backup`, `check` e `info`. A senha vem de
`duora-secrets init-backup` (`db_backup_password` para o initdb e `pgbackrest_db_pgpass` para o libpq do `backup`).

O servidor TLS do pgBackRest escuta só em `172.30.21.10:8432`: pela rede `backup-egress` a porta nem abre. Ele exige
certificado de cliente assinado pela CA própria, e `tls-server-auth=duora-postgres=duora` aceita só o certificado do
postgres e só para a stanza `duora`.

Testes: `test_db_privileges.py` (sem `trust` no `pg_hba` ativo, `duora_backup` sem superusuário e sem acesso ao banco
`duora`, recusado sem senha, recusado de `127.0.0.1` mesmo com a senha certa e recusado no socket, além dos privilégios
do `duora_app`) e o drill (abaixo).

### Saída da rede `backup-egress`

O role Ansible `backup` instala `duora-backup-egress.service`, que cria a chain `DUORA-BACKUP-EGRESS` e a liga em
`DOCKER-USER` para a subnet fixa `172.30.20.0/24` (a mesma do `compose.prod.yml`; mudar nos dois juntos):

- permite DNS (53/udp e 53/tcp) **só para os resolvedores upstream**: o resolvedor embutido do Docker encaminha a
  consulta com o IP do container, então **o acesso ao repositório depende de DNS**; sem resolução de nome o backup e
  o archive param (e o verify alerta). Os destinos vêm de `duora_backup_dns_resolvers` (inventário) ou, com a lista
  vazia (padrão), dos upstream do systemd-resolved em `/run/systemd/resolve/resolv.conf`, que é a lista para a qual o
  Docker encaminha. Sem nenhum resolvedor o filtro falha. Se o provedor trocar o resolvedor (DHCP), reaplique com
  `sudo systemctl restart duora-backup-egress`. Fixar `dns:` no serviço `backup` foi descartado: obrigaria escolher um
  resolvedor de terceiro e manter o mesmo IP no compose e no inventário;
- permite HTTPS (443/tcp) só para `duora_backup_s3_cidrs` (inventário). **Enquanto o provedor não for escolhido a
  lista é vazia, o que libera a 443 para qualquer destino**;
- descarta o resto. A rede é só IPv4.

O filtro **falha fechado**:

- a chain é trocada de uma vez (`iptables-restore`) e sempre termina em `DROP`; se faltar resolvedor ou uma regra for
  recusada, fica só o `DROP` (o backup perde a saída e o verify alerta) e a unit falha com o alerta `crit`
  (`OnFailure=duora-backup-failed@`);
- a unit roda `Before=docker.service`: no boot a chain existe antes de o Docker subir os containers com restart
  automático, sem janela em que o `backup` saia sem filtro. `PartOf=docker.service` reaplica a cada restart do Docker;
- o `duora-backup verify` (timer e deploy) confere no iptables o desvio `FORWARD -> DOCKER-USER`, o desvio da subnet
  para `DUORA-BACKUP-EGRESS` e o `DROP` final, e falha se faltar qualquer um (`EgressFilterCheckTests`). Só o drill
  local pula essa checagem (`--skip-egress-check`), porque o Docker Desktop não tem a chain do host.

O postgres não depende desse filtro: ele não está em rede nenhuma com saída. O drill prova isso (abaixo); o filtro
`DOCKER-USER` em si só pode ser provado num host Linux real (pendência).

## Configuração

| Arquivo | Onde | Conteúdo |
|---|---|---|
| `infra/images/postgres/postgresql.duora.conf` | imagem, `/etc/postgresql/` | `wal_level=replica`, `archive_mode=on`, `archive_command` do pgBackRest, `archive_timeout=60`. Incluído no `postgresql.conf` do PGDATA por `initdb/20-duora-config.sh` na primeira inicialização |
| `infra/images/postgres/pgbackrest.conf` | imagem, `/etc/pgbackrest/` | Lado do postgres: repositório remoto `backup` por TLS, `archive-async`, `archive-push-queue-max=4GiB`, spool |
| `infra/images/postgres/pgbackrest-repo.conf` | imagem, `/etc/pgbackrest/` | Lado do backup e do restore: S3, `aes-256-cbc`, retenção, servidor TLS (`tls-server-auth=duora-postgres=duora`) |
| `pgbackrest_repo.conf` | segredo do operador | endpoint, bucket, região, chaves (abaixo) |
| `pgbackrest_cipher.conf` | segredo do operador | `repo1-cipher-pass` |
| `pgbackrest_tls_*` | `duora-secrets init-backup` | CA, certificado do servidor `backup` e do cliente `duora-postgres` |

### Segredos do repositório (operador, uma vez)

O provedor ainda não foi escolhido; nada aqui presume um. Num bucket dedicado, crie uma credencial com permissão
só no prefixo `duora/` (listar, ler, gravar e apagar: o `expire` do pgBackRest apaga). Em `/etc/duora/secrets/`:

```ini
# pgbackrest_repo.conf (0400, dono 999)
[global]
repo1-s3-endpoint=<endpoint do provedor>
repo1-s3-bucket=<bucket>
repo1-s3-region=<região>
repo1-s3-key=<access key>
repo1-s3-key-secret=<secret key>
# Só se o provedor exigir: repo1-s3-uri-style=path, repo1-storage-port=<porta>
```

```ini
# pgbackrest_cipher.conf (0400, dono 999)
[global]
repo1-cipher-pass=<openssl rand -base64 48, gerado FORA da VPS>
```

```sh
sudo install -o 999 -g 999 -m 0400 pgbackrest_repo.conf pgbackrest_cipher.conf /etc/duora/secrets/
```

A senha de cifra é gerada e guardada **fora da VPS** (cofre do responsável, com cópia de emergência): perder a VPS
com a única cópia da senha torna o backup inútil (spec 18.1, "cópia inutilizável sem a chave não resolve"). O app
nunca recebe esses arquivos: só os serviços `backup` e `restore` os montam.

### Certificados TLS e senha do `duora_backup`

`duora-secrets init-backup /etc/duora/secrets` (rodado pelo Ansible, uma vez, antes do primeiro deploy) cria:

- uma CA própria (EC P-256, 10 anos);
- o certificado do servidor `backup` (SAN `DNS:backup`, `serverAuth`) e o do cliente `duora-postgres`
  (`clientAuth`), de 825 dias;
- a senha do papel `duora_backup` (`db_backup_password`) e o `.pgpass` dela (`pgbackrest_db_pgpass`).

Tudo fica em 0400 com dono 999. A chave da CA é descartada: ninguém emite certificado novo com ela.

**Rotação** (antes de 825 dias, ou se um container for comprometido): apagar os sete arquivos (`pgbackrest_tls_*`,
`db_backup_password`, `pgbackrest_db_pgpass`), rodar o comando de novo, trocar a senha no banco
(`ALTER ROLE duora_backup PASSWORD '...'` como `postgres`, pelo socket) e recriar `postgres` e `backup`
(`docker compose ... up -d --force-recreate postgres backup`). O pgBackRest recusa chave TLS com permissão de grupo
ou de outros.

## Retenção: janela recuperável de 14 dias

Agenda (timers do host, UTC, com até 15 min de jitter): **full aos domingos 03:00**, **diferencial de segunda a
sábado 03:00**, WAL contínuo (`archive_timeout=60`). Retenção no `pgbackrest-repo.conf`:

- `repo1-retention-full-type=time` + `repo1-retention-full=14`: depois de cada backup o `expire` só remove um full
  se **outro full mais novo já tiver pelo menos 14 dias**;
- WAL: sem `repo1-retention-archive*`, o pgBackRest só apaga WAL anterior ao full mais antigo que restou;
- diferenciais: sem `repo1-retention-diff`, cada um vive até o full do qual depende expirar.

A conta, com fulls F nos domingos (idade em dias no momento logo após o full de hoje):

| Momento | Fulls no repositório | Full mais antigo | Janela recuperável (até agora) |
|---|---|---|---|
| Domingo, logo depois do full | 0, 7, 14 (o de 21 expira porque o de 14 já tem 14 dias) | 14 dias | 14 dias |
| Sábado seguinte | 6, 13, 20 | 20 dias | 20 dias |

Logo, em regime, a janela fica **entre 14 e 21 dias, nunca abaixo de 14**: qualquer ponto desde o fim do full mais
antigo é recuperável com aquele full (ou um diferencial posterior) mais o WAL contínuo. Antes de completar três
semanas nada expira. Se um full semanal falhar, o anterior fica mais tempo (a janela cresce; não encolhe).

Espaço no pior momento: **3 fulls + até 18 diferenciais + 21 dias de WAL**, comprimidos (zstd) e cifrados. Exemplo
hipotético para dimensionar: full de 2 GB, 200 MB/dia de páginas alteradas e 500 MB/dia de WAL → 6 GB de fulls +
até ~12,6 GB de diferenciais (cada um cresce ao longo da semana: 0,2 + ... + 1,2 GB por semana) + 10,5 GB de WAL ≈
29 GB. Refazer com os números reais depois de uma semana de produção (`duora-backup info`).

**Tombstones (spec 12.4 e 18.4):** um registro pode voltar de um backup com até 21 dias (mais o tempo de um
restore). A retenção dos tombstones de exclusão precisa ser **pelo menos 21 dias + margem aprovada** (sugestão: 28
dias), e o restore os reaplica antes de reabrir (passo 6).

**Quebra da cadeia:** se o repositório ficar fora do ar e o WAL pendente passar de `archive-push-queue-max` (4 GiB),
o pgBackRest descarta a fila para o PostgreSQL não parar por disco cheio. Sem PITR depois desse ponto até o próximo
full: rode `sudo duora-backup full` assim que o destino voltar. O verify a cada 15 min alerta enquanto o
arquivamento falha, mas **não vê a lacuna depois que ele volta** (o `pg_stat_archiver` só mostra o último sucesso).
Quem pega a lacuna é o `verify-repo` diário (abaixo): o `pgbackrest verify` nativo lista as faixas contínuas de WAL
no repositório, e o wrapper falha se houver buraco depois do início do backup mais novo. Lacuna anterior ao backup
mais novo não alerta: o full que a resolve já existe, e o `expire` a remove com o tempo.

## Operação

`duora-backup` (root, `/usr/local/sbin`) só aceita estas ações e não repassa argumento ao pgBackRest:

| Ação | Quem chama | O que faz |
|---|---|---|
| `stanza-create` | operador, uma vez | Cria a stanza `duora` no repositório |
| `full` / `diff` | timers `duora-backup-full.timer` / `duora-backup-diff.timer` | Backup; espera um deploy em curso terminar (mesmo lock, até 30 min) |
| `verify` | `duora-backup-verify.timer` (15 min) e `duora-deploy` | filtro de saída aplicado no iptables, `pgbackrest check` (troca o segmento e espera ele chegar ao repositório), `info` sem erro, último WAL confirmado há ≤ 300 s (RPO), arquivamento não falhando, último backup há ≤ 48 h. **Falha fechado** (abaixo) |
| `verify-repo` | `duora-backup-verify-repo.timer` (diário, 05:00) | `pgbackrest verify` nativo: checksum e tamanho de cada arquivo do repositório, stanza `ok`, backup mais novo `valid`, WAL do backup mais novo presente e **nenhuma lacuna de WAL** desde o início dele (`RepositoryVerifyTests`). Espera o deploy terminar (mesmo lock) |
| `info` | operador | `pgbackrest info` |

Ninguém além do root chama o `duora-backup`: o executor de deploy não tem sudo para ele, e nenhum container tem o
socket do Docker. Falhas aparecem em `systemctl --failed`, em `journalctl -u 'duora-backup@*'` e, com prioridade
`crit`, em `journalctl -t duora-backup-alert` (`OnFailure=duora-backup-failed@`).

**Verify do timer durante um deploy.** O timer chama `duora-backup verify --scheduled`. Se o lock do `duora-deploy`
estiver ocupado por um deploy (não por um backup: `full`/`diff` seguram também `.backup.lock`) **e** houver um verify
bom há no máximo 1 h (`/var/lib/duora/.backup-verify-ok`), ele pula com uma linha informativa no journal: o deploy
recria `postgres` e `backup` e roda o próprio verify antes de parar serviços. Sem verify bom recente, com o marcador
ilegível, durante um backup ou chamado à mão, o verify roda normalmente. Testes: `ScheduledVerifyDuringDeployTests`.

**O verify falha fechado.** O deploy só segue com a prova positiva de backup saudável. Bloqueiam (código 1,
mensagem clara):

- saída do `info` vazia, JSON inválido, formato inesperado, stanza ausente ou repetida;
- campo ausente ou de outro tipo (`status.code`, `backup[].timestamp.stop`, `backup[].error` etc.);
- nenhum backup ou nenhum WAL no repositório;
- linha do `pg_stat_archiver` vazia ou ilegível, WAL mais velho que o limite, ou falha posterior ao último sucesso;
- código de saída diferente de 0 em qualquer passo, timeout, executável ausente ou exceção inesperada.

Testes: `FailClosedVerifyTests` em `scripts/deploy/test_duora_backup.py`. A sabotagem de cada tratamento para
fail-open (formato aceito como saudável, exceção virando sucesso, WAL atrasado ignorado e `duora-backup` ausente
ignorado no deploy) derruba os testes.

### Primeira configuração

1. Bucket e credencial no provedor; gravar `pgbackrest_repo.conf` e `pgbackrest_cipher.conf` (acima).
2. `ansible-playbook` (role `backup`): certificados, `duora-backup`, timers e filtro de saída.
3. Primeiro deploy pelo operador, **sem** verificação (a stanza ainda não existe):
   `sudo duora-deploy --manifest release.json --accept-missing-backup-check`. A flag não está no sudoers do
   executor de deploy: só um humano com sudo a usa.
4. `sudo duora-backup stanza-create && sudo duora-backup full && sudo duora-backup verify`.
5. Daí em diante o `duora-deploy` do CI verifica o backup antes de parar qualquer serviço e para se ele não estiver
   saudável.

## Restore seguro (spec 18.4)

Restaurar é decisão humana (spec 17.4), nunca automática. Os 10 passos, na ordem da spec:

| # | Procedimento | Como, hoje | Verificação |
|---|---|---|---|
| 1 | Isolar o incidente e escolher o ponto de recuperação | Classificar: perda de host, corrupção, exclusão acidental ou comprometimento. Ponto = instante UTC antes do dano (`pgbackrest info` mostra a janela) | Registrar a causa e o ponto escolhido |
| 2 | Ambiente limpo e rede restrita | Comprometimento: VPS nova pelo playbook, certificados TLS novos (`init-backup`), credencial S3 nova; nunca reutilizar host ou chaves suspeitos. Edge parado até o passo 10 | Só `postgres`, `backup` e `restore` no ar |
| 3 | Restaurar backup e WAL até o ponto | Ver "Comandos" abaixo | Timeline nova (`select timeline_id from pg_control_checkpoint()`), log com `recovery stopping before ...` no ponto pedido, `pg_is_in_recovery()` = false |
| 4 | Subir a aplicação em modo de recuperação | **Pendência do backend:** não existe chave que desligue e-mail, pareamento, notificações externas e jobs destrutivos. Até existir, web e worker **não sobem**; o edge fica parado | Nenhum efeito externo antes do passo 10 |
| 5 | Revogar sessões e reavaliar chaves | Revogar todas as sessões e tokens no banco restaurado (pendência do backend: comando de revogação em massa); trocar Data Protection, senhas de banco e credenciais suspeitas | Nenhuma sessão anterior ao incidente aceita |
| 6 | Aplicar tombstones posteriores ao backup | Reaplicar o registro externo de tombstones (spec 12.4) a todo registro restaurado; falha fechada se o registro estiver indisponível (pendência: o registro externo ainda não existe) | Contas e objetos excluídos não reaparecem |
| 7 | Reconciliar efeitos externos | Outbox, e-mails, uploads e verificações etárias posteriores ao ponto: conferir recibos dos fornecedores e expirar o que não tem recibo, sem reenviar em massa. **Efeitos externos continuam desligados** até aqui | Nenhum envio duplicado |
| 8 | Ajustar estados temporais | Salas e rodadas vencidas expiram; convites atrasados não são enviados (regras do domínio) | Nada recomeça "de ontem" |
| 9 | Testes de integridade e autorização | Contagens, amostras de relações, bloqueios e projeções privadas; `test_db_privileges` contra o banco restaurado; `sudo duora-backup verify` (o banco restaurado volta a arquivar) | Tudo verde |
| 10 | Liberar tráfego e jobs aos poucos | Subir web/worker/edge pelo `duora-deploy` da release corrente; acompanhar erros, duplicação e atraso | Registrar RPO e RTO observados (spec 18.5) |

### Comandos (passo 3)

Na VPS, com os dois env files da release corrente (como em [deploy.md](deploy.md#operação-manual)):

```sh
S=/var/lib/duora
ID=$(sudo python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["releaseId"])' $S/current.json)
C="sudo docker compose -f /opt/duora/compose.prod.yml --env-file /etc/duora/duora.env --env-file $S/releases/$ID.env"

$C stop web worker edge                    # passo 2: nada fala com o banco nem com a internet
$C rm --stop --force postgres backup       # o backup monta o PGDATA só leitura e sai junto
sudo docker volume rm duora_pgdata         # só depois de decidir que o volume atual não serve (copie antes se houver dúvida)
$C --profile restore run --rm restore --type=time --target="2026-10-15 18:00:00+00" --target-action=promote restore
$C up -d --wait backup                     # o restore_command do postgres busca o WAL nele
$C up -d --wait postgres                   # recovery até o ponto e promoção; acompanhe com $C logs -f postgres
```

Sem `--type=time`, o restore vai até o fim do WAL disponível (perda de host). Para manter o volume atual e
restaurar só o que mudou, use `--delta` no lugar de apagar o volume.

## Drill local (prova automática)

`scripts/backup/restore_drill.py` sobe o `compose.prod.yml` real (postgres, backup e o job restore, com os mesmos
secrets, redes e volumes) mais `scripts/backup/compose.drill.yml`, que acrescenta um **MinIO só como dublê S3 de
teste** (nunca produção) e um host `internet` arbitrário na rede de saída do backup:

```sh
docker buildx build -f infra/images/postgres/Dockerfile -t duora-postgres:drill --load .
python3 scripts/backup/restore_drill.py --image duora-postgres:drill
```

1. **Isolamento**:
   - o postgres não conecta no host `internet` (nem por nome, nem pelo IP) nem no S3, e conecta no servidor TLS do
     backup;
   - o backup conecta no S3;
   - outro container (`internet`) não alcança a porta TLS do backup (por nome e pelo IP da `backup-egress`) nem o
     postgres;
   - o postgres não enxerga credencial do repositório;
   - `duora_backup` autentica do backup com a senha e falha sem ela.
2. `stanza-create` e full pelo `duora-backup`; escrita das linhas `before`; marca o ponto; escrita das `after`.
3. **RPO natural**: tempo entre o commit das `after` e o WAL delas confirmado no repositório, só com
   `archive_timeout` (sem troca forçada).
4. `duora-backup verify`; destrói containers e volume; restore PITR pelo job `restore`; promoção.
5. Confere `before` = 100 e `after` = 0, timeline ≥ 2, e `duora-backup verify` de novo.

**RTO observado** = da destruição até os dados conferidos. Saída em JSON (`--report arquivo.json`); `--keep`
mantém o ambiente. O CI roda o drill no job `images` (amd64 e arm64).

| Data | Ambiente | RPO observado | RTO observado | Observação |
|---|---|---|---|---|
| 2026-10-15 | Docker Desktop, Apple Silicon (arm64), banco quase vazio | 55,3 s | 31,2 s | Prova do caminho, não do volume real: o RTO de produção depende do tamanho do banco e da banda até o S3 |
| 2026-10-15 | Idem, com prova de isolamento | 55,6 s | 13,4 s | O RPO natural fica perto do `archive_timeout` (60 s), dentro da meta de 5 min |
| 2026-10-15 | Idem, sem `trust`, sem socket compartilhado, `duora_backup` sem superusuário | 55,6 s | 13,1 s | Backup, check e restore com o papel mínimo |

Limites do drill: o MinIO roda na mesma máquina (sem latência de rede real); o filtro `DOCKER-USER` não é exercido
(Docker Desktop não tem a chain do host); dados sintéticos.

## Alertas (spec 19.1)

| Sinal | Hoje | Falta |
|---|---|---|
| WAL atrasado ou arquivamento falhando | `duora-backup verify` a cada 15 min falha (unit em `failed`) | Destino de alerta fora da VPS (OTel Collector ou monitor externo lendo o estado das units) |
| Lacuna na cadeia de WAL ou arquivo corrompido no repositório | `duora-backup verify-repo` diário falha (unit em `failed`, registro `crit`) | Idem |
| Backup full/diff falhou | Unit `duora-backup@full`/`@diff` em `failed` e registro `crit` em `duora-backup-alert` | Idem |
| Fila de WAL no pg_wal | Aviso do pgBackRest no log do postgres | Métrica do tamanho do `pg_wal` com alerta antes de 4 GiB |
| Certificado TLS perto de vencer | — | Checagem de validade (825 dias) no verify ou no monitor |

## Pendências que dependem da VPS ou do fornecedor

- **Provedor S3**: escolher (com região e jurisdição compatíveis com a spec 12), criar bucket e credencial restrita
  ao prefixo, preencher `pgbackrest_repo.conf` e os CIDRs do endpoint em `duora_backup_s3_cidrs`.
- **Cópia protegida contra exclusão** (spec 18.3, "resiliência a comprometimento"): a credencial do `backup`
  precisa apagar para o `expire` funcionar. Falta uma segunda cópia com retenção administrada fora da VPS (bucket de
  outra conta com replicação, ou versionamento com lifecycle maior que 21 dias). Object lock no bucket operacional
  conflita com o `expire` e exigiria adaptar a política.
- **Prova no host real**: rodar o playbook, conferir a chain `DUORA-BACKUP-EGRESS` (`iptables -S DOCKER-USER`), uma
  conexão de dentro do `backup` a um destino fora da allowlist falhando, restore completo com o volume real e o RTO
  cronometrado (spec 18.5), e o ensaio mensal de perda de VPS.
- **Restore isolado semanal automático** (spec 18.5) num ambiente de recuperação restrito, com a cópia e os segredos
  removidos depois.
- **Alertas** fora da VPS (tabela acima).
- **Backend** (passos 4 a 6 do restore): modo de recuperação com efeitos externos desligados, revogação em massa de
  sessões, registro externo de tombstones.
