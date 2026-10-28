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
| **(a) Repository host via `pgbackrest server` (escolhida)** | O postgres fica só em redes `internal`, sem nenhuma rota para fora; credenciais do S3 e senha de cifra saem do processo do banco (um postgres comprometido não as lê nem fala direto com o S3); o filtro de saída só precisa cuidar de um container sem SQL exposto | Um container a mais; CA e certificados próprios para gerar, guardar e rotacionar (825 dias); o restore usa um job separado. **Não protege contra exclusão:** com o certificado de cliente, um postgres comprometido lista e apaga o repositório pelo servidor TLS (abaixo) |
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

**O que a topologia protege e o que não protege.** Ela protege a saída (o postgres não tem rota para fora) e a
credencial do S3 e a senha de cifra (só o `backup` as tem). Ela **não** protege o repositório contra exclusão:
`tls-server-auth` restringe a stanza, não as operações, e com o certificado de cliente o postgres roda `repo-ls`,
`repo-rm --recurse` e `expire` no repositório pelo servidor TLS. Um postgres comprometido apaga backups e WAL. A
proteção contra exclusão é o versionamento + Object Lock do bucket, **pré-requisito de produção** (próxima seção). O
drill prova as duas coisas: o `repo-ls` e o `repo-rm --recurse` vindos do postgres funcionam, e o restore lê as
versões anteriores à exclusão.

### Proteção contra exclusão: versionamento + Object Lock (pré-requisito de produção)

Decidido pelo usuário em 2026-10-15 ([ADR-0023](../architecture/adr/0023-topologia-do-backup-repository-host-e-object-lock.md),
spec 18.3, "resiliência a comprometimento"). Nenhum deploy de produção acontece sem isto: o `duora-backup verify`
(timer e deploy) **falha fechado** se o bucket não provar versionamento + Object Lock.

Requisitos do bucket. Provedor escolhido: **Backblaze B2, região US East** (responsável, 2026-10-16; adendo do
ADR-0023); criação do bucket, das chaves e validação em [backblaze-setup.md](backblaze-setup.md):

| Requisito | Valor | Por quê |
|---|---|---|
| Versionamento | `Enabled` | Uma exclusão (do `expire` ou de um atacante) vira delete marker; a versão anterior continua lá |
| Object Lock | habilitado (no B2, "File Lock", ligado na criação do bucket; não pode ser desligado depois) | Nem a credencial do `backup` nem um atacante com ela apagam uma versão antes do prazo |
| Retenção padrão | `COMPLIANCE`, **≥ 21 dias**. O verify recusa `GOVERNANCE`, que cede a quem tiver `s3:BypassGovernanceRetention` | Cobre a janela máxima de retenção do repositório (21 dias, seção de retenção) |
| Lifecycle | expirar **versões não correntes** depois de 21 dias + margem (30 dias; no B2, `daysFromHidingToDeleting: 30` no prefixo `duora/`) | Sem isso as versões apagadas pelo `expire` ficam para sempre e o custo cresce sem limite |

O `expire` do pgBackRest continua funcionando: num bucket versionado, apagar sem `versionId` só cria um delete
marker (no B2, *hide marker*), o que o Object Lock permite. Custo: as versões não correntes ficam até o lifecycle removê-las, então o
espaço pode chegar perto do dobro da conta da seção de retenção.

Chave do `backup` no B2 (application key `duora-backup`): o B2 não tem política de bucket nem IAM, então a restrição
mora toda na chave, criada pelo responsável com o CLI do B2 ([backblaze-setup.md](backblaze-setup.md#3-chaves)):

- presa **só a este bucket** e ao prefixo `duora/` (o `repo1-path=/duora`);
- capacidades `listFiles`, `readFiles`, `writeFiles` (inclui apagar **sem** versão, que vira hide marker),
  `readBuckets` (o verify lê o versionamento) e `readBucketRetentions` (o verify lê o File Lock);
- **sem** `deleteFiles` (apagar versão), `bypassGovernance`, `writeFileRetentions`, `writeBucketRetentions`,
  `writeFileLegalHolds`, `writeBuckets` (lifecycle, lock), `deleteBuckets`, `shareFiles` e qualquer capacidade de
  chaves. Apagar versão e mexer em lifecycle ficam com a chave administrativa, **fora da VPS**.

A conta B2 é compartilhada com outro projeto: uma chave desse projeto sem restrição de bucket e com `deleteFiles`
apaga versões do Duora fora do lock. Confira (passo 6 do runbook) que toda chave da conta está presa ao próprio
bucket.

Como o verify confere: lê o `repo.conf` montado no `backup`, assina (AWS Signature V4, que o B2 aceita na API S3) um
`GET ?versioning` e um `GET ?object-lock` e os envia de dentro do próprio container por `openssl s_client` (mesma
rede, DNS, CA e filtro de saída do pgBackRest, com verificação de certificado e de nome). Com endpoint
`*.backblazeb2.com`, antes disso chama o `b2_authorize_account` (API nativa v4, `api.backblazeb2.com`) com a mesma
chave e falha se ela tiver capacidade fora da lista acima, faltar uma das necessárias, não estiver presa só a este
bucket, não tiver o prefixo `duora/` ou se o `s3ApiUrl` da conta não for o `repo1-s3-endpoint` (número da região
errado). O corpo dessa resposta traz um token de sessão e nunca é impresso. Bloqueiam: versionamento diferente de `Enabled`,
Object Lock ausente (`404 ObjectLockConfigurationNotFoundError`) ou desligado, sem retenção padrão, retenção menor
que 21 dias, modo diferente de `COMPLIANCE`, resposta HTTP diferente de 200 (por exemplo 403 por falta de permissão)
ou resposta ilegível. Testes: `BucketProtectionTests`, `BucketProtectionActionTests`, o vetor de assinatura da
documentação AWS em `SignedRequestTests` e, com respostas no formato do B2, `B2RepoSettingsTests`,
`B2KeyProblemsTests` e `B2BucketProtectionActionTests`.

Limites da consulta: só **chave compartilhada** (`repo1-s3-key-type=shared`, `repo1-s3-key` e
`repo1-s3-key-secret`). `repo1-s3-key-type=auto` (papel de instância), `repo1-s3-token` (credencial temporária) e
`repo1-storage-host` (conectar num host diferente do endpoint) não são tratados: com eles a consulta assina ou
conecta errado e o verify falha fechado. Se o provedor escolhido exigir um deles, a consulta precisa ser estendida.

**O Object Lock é defesa em profundidade, não a principal.** A retenção conta desde a criação de cada versão, não
desde a exclusão: um objeto gravado há mais de 21 dias (um full antigo, o `backup.info` de três semanas) já está fora
do lock e uma credencial com permissão de apagar versão (`deleteFiles` no B2) apagaria a versão na hora. A defesa
principal são as **capacidades da chave**: a do `backup` (a única na VPS) não apaga versão nem mexe em retenção,
versionamento, lock ou lifecycle, e o verify confere isso a cada execução. O lock cobre o caso de essa negação falhar (política mal aplicada, credencial administrativa vazada) para
os objetos dos últimos 21 dias, que são os de que o restore mais recente depende.

**Recuperação depois de uma exclusão maliciosa** (passo 3 do restore, com o repositório apagado):

1. Restaurar o banco lendo o repositório como estava antes do ataque:
   `--repo=1 --repo-target-time="<instante UTC antes da exclusão>"` no comando do job `restore` (abaixo). O
   `archive-get` da recuperação usa o mesmo instante.
2. Com a chave administrativa, fora da VPS, remover os hide markers posteriores ao ataque (só eles; nunca versões;
   comando em [backblaze-setup.md](backblaze-setup.md#recuperação-depois-de-uma-exclusão-maliciosa)). O repositório
   volta ao estado anterior e o banco restaurado volta a arquivar nele.
3. Trocar certificados TLS e a chave do B2 (o postgres que apagou estava comprometido) e rodar
   `sudo duora-backup verify` e `sudo duora-backup verify-repo`.

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
- permite HTTPS (443/tcp) só para `duora_backup_s3_cidrs` (inventário): os blocos IPv4 da Backblaze, que servem a API
  S3 e a `api.backblazeb2.com` do verify ([backblaze-setup.md](backblaze-setup.md#5-filtro-de-saída)).
  **Lista vazia (o padrão) libera a 443 para qualquer destino**; mesmo preenchida, ela libera toda a Backblaze,
  inclusive buckets de terceiros;
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

Bucket e chave `duora-backup` criados pelo responsável ([backblaze-setup.md](backblaze-setup.md)). O
`pgbackrest_repo.conf` é gravado por `duora-secrets write-backup-repo`, com a chave lida do stdin (nunca no
histórico do shell; o comando exato está no passo 4 do runbook do B2). Fica assim, em `/etc/duora/secrets/`:

```ini
# pgbackrest_repo.conf (0400, dono 999)
[global]
repo1-s3-endpoint=s3.us-east-005.backblazeb2.com   # confirmar o número no bucket (Endpoint)
repo1-s3-bucket=<bucket de backup>
repo1-s3-region=us-east-005                        # o 2o rótulo do endpoint
repo1-s3-uri-style=host
repo1-s3-key=<keyID da chave duora-backup>
repo1-s3-key-secret=<applicationKey da chave duora-backup>
```

```ini
# pgbackrest_cipher.conf (0400, dono 999)
[global]
repo1-cipher-pass=<openssl rand -base64 48, gerado FORA da VPS>
```

```sh
# O diretório é criado pelo playbook (role app_dirs, root, 0700); o install -d só garante o mesmo estado se ainda não
# existir, sem afrouxar o modo.
sudo install -d -o root -g root -m 0700 /etc/duora/secrets
sudo install -o 999 -g 999 -m 0400 pgbackrest_cipher.conf /etc/duora/secrets/
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

**Repositório fora do ar não derruba o banco.** `postgres`, `backup` e `restore` rodam com `init: true` (o tini do
Docker é o PID 1). Sem isso o postmaster é o PID 1, adota o processo assíncrono do `archive-push` do pgBackRest e,
quando ele sai com erro (S3 parado), o trata como filho desconhecido ("untracked child process ... exited with exit
code 103") e faz crash-restart do cluster a cada tentativa, e o shutdown normal acaba em "abnormal database system
shutdown". O drill prova a correção (passo 5 do drill) e falha sem o `init`. O `postgres` depende do `backup`
(`depends_on`, `service_started`): o Compose sobe o `backup` antes e para o `postgres` antes dele. O healthcheck do
`postgres` é `pg_isready -h 127.0.0.1 -U duora_app -d postgres`: só fica healthy com o servidor aceitando conexões
(não durante o recovery, ao contrário de uma sonda TCP, e web, worker e migrator esperam `service_healthy`), pelo
TCP só depois do initdb, e sem FATAL no log (como `postgres` pela rede o `pg_hba` recusava e logava um FATAL a cada
5 s; `duora_app` é aceito e o `pg_isready` não manda senha). O drill falha se aparecer FATAL de `pg_hba` ou de
`duora_app` no log.

**Quebra da cadeia:** se o repositório ficar fora do ar e o WAL pendente passar de `archive-push-queue-max` (4 GiB),
o pgBackRest descarta a fila para o PostgreSQL não parar por disco cheio. Sem PITR depois desse ponto até o próximo
full: rode `sudo duora-backup full` assim que o destino voltar. O verify a cada 15 min alerta enquanto o
arquivamento falha, mas **não vê a lacuna depois que ele volta** (o `pg_stat_archiver` só mostra o último sucesso).
Quem pega a lacuna é o `verify-repo` diário (abaixo): ele monta as faixas contínuas de WAL pela listagem do archive
(`repo-ls`, só nomes) e falha se houver buraco depois do início do backup mais novo. O `pgbackrest verify` nativo não
falha com lacuna, e com `--set` nem olha a cadeia, por isso a decisão é do wrapper. Lacuna anterior ao backup mais
novo não alerta: o full que a resolve já existe, e o `expire` a remove com o tempo.

**Custo de leitura da verificação** (decidido pelo usuário em 2026-10-15: diária leve e completa semanal). O
`pgbackrest verify` baixa e decifra cada arquivo que confere:

- diário (`verify-repo`): a listagem do archive (requisições `LIST`, sem baixar) e o `verify --set` só do backup
  mais novo e do WAL dele. Leitura de um full ou de um diferencial por dia;
- semanal (`verify-repo-full`, quarta 06:00 UTC, longe do full de domingo e depois do reinício automático das 04:30):
  o repositório inteiro, o que no pior momento chega a **3 fulls + 18 diferenciais + 21 dias de WAL** (no exemplo de
  dimensionamento acima, ~29 GB por semana). No B2 essa leitura é download (~165 GB por mês somando o diário e um
  ensaio de restore), gratuito até 3× o armazenamento médio do mês e cobrado por GB acima disso
  ([backblaze-setup.md](backblaze-setup.md#custo-esperado)).

## Operação

`duora-backup` (root, `/usr/local/sbin`) só aceita estas ações e não repassa argumento ao pgBackRest:

| Ação | Quem chama | O que faz |
|---|---|---|
| `stanza-create` | operador, uma vez | Cria a stanza `duora` no repositório |
| `full` / `diff` | timers `duora-backup-full.timer` / `duora-backup-diff.timer` | Backup; espera um deploy em curso terminar (mesmo lock, até 30 min) |
| `verify` | `duora-backup-verify.timer` (15 min) e `duora-deploy` | filtro de saída aplicado no iptables, `pgbackrest check` (troca o segmento e espera ele chegar ao repositório), `info` sem erro, último WAL confirmado há ≤ 300 s (RPO), arquivamento não falhando, último backup há ≤ 48 h. **Falha fechado** (abaixo) |
| `verify-repo` | `duora-backup-verify-repo.timer` (diário, 05:00) | Leve: `pgbackrest verify --set` do backup mais novo (checksum e tamanho dos arquivos dele e do WAL dele), stanza `ok`, backup `valid`, WAL do backup presente e **nenhuma lacuna de WAL** desde o início dele pela listagem do archive (`RepositoryVerifyTests`). Espera o deploy terminar (mesmo lock) |
| `verify-repo-full` | `duora-backup-verify-repo-full.timer` (quarta, 06:00) | O mesmo, com o `pgbackrest verify` de **todo** o repositório (todos os backups e todo o WAL) |
| `info` | operador | `pgbackrest info` |

Ninguém além do root chama o `duora-backup`: o executor de deploy não tem sudo para ele, e nenhum container tem o
socket do Docker. Falhas aparecem em `systemctl --failed`, em `journalctl -u 'duora-backup@*'` e, com prioridade
`crit`, em `journalctl -t duora-backup-alert` (`OnFailure=duora-backup-failed@`).

**Verify do timer durante um deploy.** O timer chama `duora-backup verify --scheduled`. Se o lock do `duora-deploy`
estiver ocupado por um deploy (não por um backup: `full`/`diff` seguram também `.backup.lock`) **e** houver um verify
bom há no máximo 1 h (`/var/lib/duora/.backup-verify-ok`), ele pula com uma linha informativa no journal: o deploy
recria `postgres` e `backup` e roda o próprio verify antes de parar serviços. Sem verify bom recente, com o marcador
ilegível, durante um backup ou chamado à mão, o verify roda normalmente. Testes: `ScheduledVerifyDuringDeployTests`
e `ProcLocksTests`.

No Linux o lock é lido em `/proc/locks` (sem tomá-lo), casando dispositivo e inode do `stat` de
`/var/lib/duora/.deploy.lock`. Em btrfs (ou num subvolume), o dispositivo do `stat` pode não ser o que o
`/proc/locks` mostra; aí o deploy não é reconhecido e o verify **roda** durante ele, o que pode dar um alerta `crit`
falso enquanto `postgres` e `backup` são recriados. O erro é sempre para o lado de alertar, nunca de esconder uma
falha. Confira na VPS com `findmnt -T /var/lib/duora` (ext4 casa).

**Estatísticas após o check.** O PostgreSQL pode publicar `pg_stat_archiver` depois de o `pgbackrest check`
confirmar a chegada do WAL. Com repositório válido, o verify faz no máximo seis consultas, em transações separadas,
com cinco pausas de 1 s e timeout de 5 s por consulta. Não repete o check nem aumenta o RPO de 300 s. Estatísticas
ilegíveis, erro de SQL ou repositório inválido falham imediatamente; atraso ou falha de archive persistente ao fim
das releituras também bloqueia. Testes: `ArchiverStatsLagTests`. Referência: [estatísticas cumulativas do PostgreSQL](https://www.postgresql.org/docs/18/monitoring-stats.html#MONITORING-STATS-VIEWS).

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

1. Bucket e chaves no Backblaze B2, pelo responsável ([backblaze-setup.md](backblaze-setup.md), passos 1 a 3).
2. `ansible-playbook` (roles `app_dirs` e `backup`): `/etc/duora/secrets`, certificados, `duora-backup`, timers e
   filtro de saída.
3. Gravar `pgbackrest_repo.conf` (`duora-secrets write-backup-repo`) e `pgbackrest_cipher.conf` em
   `/etc/duora/secrets/` (acima), já com o diretório criado pelo passo 2, e os CIDRs da Backblaze no inventário.
4. Primeiro deploy pelo operador, **sem** verificação (a stanza ainda não existe):
   `sudo duora-deploy --manifest release.json --accept-missing-backup-check`. A flag não está no sudoers do
   executor de deploy: só um humano com sudo a usa.
5. `sudo duora-backup stanza-create && sudo duora-backup full && sudo duora-backup verify`.
6. Daí em diante o `duora-deploy` do CI verifica o backup antes de parar qualquer serviço e para se ele não estiver
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
$C stop postgres                           # primeiro o banco: o fast shutdown ainda arquiva no backup de pé
$C rm --stop --force postgres backup       # o backup monta o PGDATA só leitura e sai em seguida
sudo docker volume rm duora_pgdata         # só depois de decidir que o volume atual não serve (copie antes se houver dúvida)
$C --profile restore run --rm restore --type=time --target="2026-10-15 18:00:00+00" --target-action=pause restore
$C up -d --wait backup                     # o restore_command do postgres busca o WAL nele
$C up -d --wait postgres                   # recovery até o ponto e PAUSA; acompanhe com $C logs -f postgres
P="$C exec --no-TTY postgres psql --username postgres --dbname duora --tuples-only --no-align --command"
$P "select pg_is_in_recovery(), pg_get_wal_replay_pause_state()"   # t|paused: parado no ponto, ainda sem promover
# Conferir aqui, só leitura: o dano não está, o que deve estar está (contagens, amostras). Ponto errado = parar o
# postgres, apagar o volume e repetir o restore com outro --target; nada foi promovido nem arquivado na timeline nova.
$P "select pg_wal_replay_resume()"         # promove: timeline nova
$P "select pg_is_in_recovery()"            # f
sudo duora-backup full                     # full logo depois do PITR (abaixo)
```

`--target-action=pause` (em vez de `promote`) deixa o banco parado no ponto escolhido, aceitando só leitura, para
conferir antes de promover: promover cria uma timeline nova e é o ponto sem volta do restore. O drill usa `promote`
porque confere os dados por script.

**Full depois do PITR:** a timeline nova só tem WAL a partir da promoção, e o backup mais novo é de antes do ponto.
Um `sudo duora-backup full` logo depois da promoção dá à timeline nova um backup próprio: um segundo restore não
precisa atravessar a troca de timeline, e o restore seguinte fica mais curto (RTO).

Sem `--type=time`, o restore vai até o fim do WAL disponível (perda de host). Para manter o volume atual e
restaurar só o que mudou, use `--delta` no lugar de apagar o volume. Depois de uma exclusão maliciosa do repositório,
acrescente `--repo=1 --repo-target-time="<instante UTC antes da exclusão>"` (seção "Proteção contra exclusão").

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
2. Bucket do dublê criado com versionamento e Object Lock `COMPLIANCE` de 21 dias (sem isso o verify recusa);
   `stanza-create` e full pelo `duora-backup`; escrita das linhas `before`; marca o ponto; escrita das `after`.
3. **RPO natural**: tempo entre o commit das `after` e o WAL delas confirmado no repositório, só com
   `archive_timeout` (sem troca forçada).
4. `duora-backup verify` (inclui o versionamento e o Object Lock do bucket).
5. **Repositório fora do ar**: para o `s3` por 30 s enquanto escreve e troca segmentos; o banco precisa responder
   o tempo todo e o log do postgres não pode ter nenhum `terminating any other active server processes`
   (crash-restart). Depois o `verify` passa de novo, com o arquivamento recuperado sozinho.
6. **Postgres comprometido**: com o próprio certificado de cliente, o postgres lista (`repo-ls`) e apaga
   (`repo-rm --recurse archive` e `backup`) o repositório. O drill **exige** que isso funcione: documenta o poder que
   a topologia não tira. Se um dia falhar, esta página e o ADR-0023 precisam ser revistos. Em seguida, uma versão
   que sobrou recusa `mc rm --version-id` (Object Lock `COMPLIANCE`), mesmo vinda do usuário root do dublê.
7. Destrói containers e volume; restore PITR pelo job `restore` lendo as versões anteriores à exclusão
   (`--repo-target-time`); promoção. Confere `before` = 100 e `after` = 0 e timeline ≥ 2.
8. Desfaz a exclusão (no dublê, `mc undo --action DELETE`; na produção, remoção dos delete markers com a credencial
   administrativa) e roda `duora-backup verify`, `verify-repo` e `verify-repo-full`.
9. Apaga um segmento de WAL no meio da cadeia e exige que o `verify-repo` acuse a lacuna.

**RTO observado** = da destruição até os dados conferidos. Saída em JSON (`--report arquivo.json`); `--keep`
mantém o ambiente. Sem `--keep`, o drill apaga o diretório temporário dele (segredos sintéticos com dono 999) por um
container root da própria imagem e **falha** se algo sobrar. O CI roda o drill no job `images` (amd64 e arm64).

| Data | Ambiente | RPO observado | RTO observado | Observação |
|---|---|---|---|---|
| 2026-10-15 | Docker Desktop, Apple Silicon (arm64), banco quase vazio | 55,3 s | 31,2 s | Prova do caminho, não do volume real: o RTO de produção depende do tamanho do banco e da banda até o S3 |
| 2026-10-15 | Idem, com prova de isolamento | 55,6 s | 13,4 s | O RPO natural fica perto do `archive_timeout` (60 s), dentro da meta de 5 min |
| 2026-10-15 | Idem, sem `trust`, sem socket compartilhado, `duora_backup` sem superusuário | 55,6 s | 13,1 s | Backup, check e restore com o papel mínimo |
| 2026-10-15 | Idem, bucket com versionamento + Object Lock, repositório apagado pelo postgres | 56,0 s | 13,6 s | Restore pelas versões anteriores à exclusão (`--repo-target-time`); `verify-repo` acusou o segmento apagado |

Limites do drill: o MinIO roda na mesma máquina (sem latência de rede real); o filtro `DOCKER-USER` não é exercido
(Docker Desktop não tem a chain do host); dados sintéticos. O MinIO continua listando como "diretório" um prefixo que
só tem delete markers, então lá um `stanza-create` depois da exclusão recusa ("archive directory not empty"); por isso
o drill desfaz a exclusão em vez de recriar a stanza, que é também o procedimento recomendado.

## Alertas (spec 19.1)

| Sinal | Hoje | Falta |
|---|---|---|
| WAL atrasado ou arquivamento falhando | `duora-backup verify` a cada 15 min falha (unit em `failed`) | Destino de alerta fora da VPS (OTel Collector ou monitor externo lendo o estado das units) |
| Lacuna na cadeia de WAL ou arquivo corrompido no repositório | `duora-backup verify-repo` diário (backup mais novo e cadeia) ou `verify-repo-full` semanal (tudo) falha (unit em `failed`, registro `crit`) | Idem |
| Backup full/diff falhou | Unit `duora-backup@full`/`@diff` em `failed` e registro `crit` em `duora-backup-alert` | Idem |
| Fila de WAL no pg_wal | Aviso do pgBackRest no log do postgres | Métrica do tamanho do `pg_wal` com alerta antes de 4 GiB |
| Certificado TLS perto de vencer | — | Checagem de validade (825 dias) no verify ou no monitor |

## Backup das fotos (restic)

Decisão do responsável em 2026-10-16 (adendo do [ADR-0010](../architecture/adr/0010-object-storage-externo-leitura-via-api.md)):
as fotos ficam no disco da VPS (`duora_media_dir`, padrão `/var/lib/duora-media`) e os **derivados** vão todo dia,
com `restic`, para o prefixo `media/` do mesmo bucket B2 com File Lock. A quarentena (original com EXIF, no máximo
24 h) não entra. O pgBackRest não serve aqui: ele só copia o cluster do PostgreSQL.

| O quê | Valor |
|---|---|
| Ferramenta | `restic` do Ubuntu 24.04 (pacote `restic`, BSD-2-Clause), instalado pelo role `backup` |
| Onde roda | No host, como root, pelos timers `duora-backup-media*.timer` → `duora-media-backup@media-*.service`, sem dependência do Docker (falha dispara o mesmo `duora-backup-failed@`) |
| Destino | `s3:https://<endpoint>/<bucket>/media`, cifrado no cliente com a senha do repositório |
| Chave | `duora-media-backup`, presa a `media/`, sem `deleteFiles` ([backblaze-setup.md](backblaze-setup.md#3-chaves)) |
| Retenção | 14 dias de snapshots (`forget --keep-within 14d --prune`) + 21 dias de lock + 30 dias do lifecycle: uma foto apagada some da VPS na hora e da cópia externa em até ~65 dias |
| Diário 02:30 UTC | `media-backup`: `restic backup` dos derivados e `forget --prune` |
| Diário 05:30 UTC | `media-verify`: bucket e chave provados como no verify do banco (prefixo `media/`), snapshot mais novo com no máximo 26 h e `restic check` da estrutura |
| Semanal (quinta 06:30 UTC) | `media-check`: `restic check --read-data`, baixa e confere todos os pacotes |

Segredos, gravados pelo `duora-secrets write-media-backup-repo` (root, 0400, em `/etc/duora/secrets`):
`restic_media_repository` (destino), `restic_media_password` (senha do repositório, gerada **fora** da VPS com
`openssl rand -base64 48` e guardada no cofre: sem ela a cópia é ilegível) e `restic_media_credentials` (a chave B2 no
formato de credenciais compartilhadas). O restic lê os três por arquivo (`RESTIC_REPOSITORY_FILE`,
`RESTIC_PASSWORD_FILE`, `AWS_SHARED_CREDENTIALS_FILE`); nenhum segredo vai à linha de comando nem ao ambiente.

### Primeira configuração das fotos

1. Chave `duora-media-backup` no B2 ([backblaze-setup.md](backblaze-setup.md#3-chaves)) e senha do repositório no
   cofre.
2. Na VPS, gravar os três arquivos:

   ```sh
   ( IFS= read -r -p 'keyID: ' key; IFS= read -r -s -p 'applicationKey: ' secret; echo >&2
     IFS= read -r -s -p 'senha do restic: ' password; echo >&2
     printf '%s\n%s\n%s\n' "$key" "$secret" "$password" ) \
     | sudo duora-secrets write-media-backup-repo /etc/duora/secrets --bucket <BUCKET> \
         --endpoint s3.us-east-005.backblazeb2.com   # o endpoint anotado no bucket
   ```

3. `duora_media_backup_enabled: true` no inventário e o playbook (instala o restic e liga os timers).
4. `sudo duora-backup media-init` (recusa se o bucket não provar File Lock `COMPLIANCE` ≥ 21 dias e se a chave não
   estiver presa a `media/` com as capacidades mínimas), depois `sudo duora-backup media-backup` e
   `sudo duora-backup media-verify`.

O upload de fotos **não liga em produção** antes destes quatro passos e de um restore de ensaio (abaixo) bem-sucedido.
O interruptor é `duora_media_review` no inventário (vira `DUORA_MEDIA_REVIEW` no `duora.env`); com ele preenchido, o
`duora-deploy` recusa a release, antes de baixar imagens, se o `duora-backup-media-verify.timer` não estiver ativo ou
se o último `media-verify` bom (marcador `.media-verify-ok` em `/var/lib/duora`) tiver mais de 30 h
(`MediaUploadGateTests`).

**Limite da proteção das fotos:** o restic deduplica, e um pacote gravado no primeiro backup continua sendo usado por
todos os snapshots seguintes. O File Lock conta da gravação de cada versão: um pacote com mais de 21 dias está fora do
lock, e o que o protege é só a chave da VPS não ter `deleteFiles`. Um `restic forget --prune` malicioso com essa chave
só oculta (hide marker), e a recuperação remove os hide markers; uma chave com `deleteFiles` vazada apagaria esses
pacotes de vez. Mitigação registrada como lacuna, **não implementada**: um `restic check` independente, de fora da VPS,
com uma chave só de leitura de `media/`, para notar a falta de um pacote antes de ela virar perda.

### Restore das fotos

`media-restore` só grava num diretório de staging absoluto, vazio e fora de `duora_media_dir`:

```sh
# Diretório das fotos deste host: o DUORA_MEDIA_DIR que o app_dirs gravou (duora_media_dir do inventário).
M=$(sudo sed -n 's/^DUORA_MEDIA_DIR=//p' /etc/duora/duora.env)
sudo duora-backup media-restore --media-root "$M" --target /var/tmp/duora-media-restore
```

Os timers passam `--media-root {{ duora_media_dir }}` pela unit `duora-media-backup@.service`; à mão, sem
`--media-root`, o padrão é `/var/lib/duora-media`, por isso os comandos desta seção usam `$M`.

O restic recria o caminho original dentro do staging (`/var/tmp/duora-media-restore$M/derivatives`).
**Antes de copiar** para o diretório em uso, aplique as exclusões do banco já restaurado (INV-11; o banco vem
primeiro, com os tombstones): só voltam os derivados de assets `Approved`.

```sh
# $C é o mesmo "sudo docker compose ..." com os env files da release, da seção de restore do banco acima.
$C exec --no-TTY postgres psql --username postgres --dbname duora --tuples-only --no-align --command \
  "select v.object_key from media.media_variants v join media.media_assets a on a.id = v.media_asset_id
   where a.state = 'Approved'" \
  | grep -E '^derivatives/[0-9a-f]{32}-(thumb|card|full)$' > /var/tmp/duora-media-keep.txt   # só chaves nossas
cd "/var/tmp/duora-media-restore$M"
sudo rsync -a --files-from=/var/tmp/duora-media-keep.txt ./ "$M/"
sudo chown -R 1654:1654 "$M/derivatives"
sudo rm -rf /var/tmp/duora-media-restore /var/tmp/duora-media-keep.txt
```

A chave de objeto é `derivatives/<token>-<variante>`, relativa ao `duora_media_dir`; o `grep` deixa passar só esse
formato (o mesmo regex do `ObjectKey`), para uma linha estranha vinda do banco nunca virar caminho do `rsync`, e o
`--files-from` copia só a lista. O marcador `.duora-media-area` de cada área não volta do backup: quem o grava é o
playbook (role `app_dirs`). Um asset aprovado sem arquivo no backup volta como 404 na leitura (ADR-0025, item "Backup").

Testes: `MediaBackupTests` (comandos do restic, segredos só por arquivo, diretório ausente, prune só depois de backup
bom, bucket e chave provados no host, idade do snapshot na fronteira, listagem ilegível, init só em bucket protegido,
alvo do restore), `MediaBackupUnitsTests` (timers, alerta, padrões do inventário) e
`WriteMediaBackupRepositoryTests`.

## Pendências que dependem da VPS ou do fornecedor

- **Provedor S3**: escolhido (Backblaze B2, US East, 2026-10-16). Falta o responsável criar bucket e chaves
  ([backblaze-setup.md](backblaze-setup.md)), gravar `pgbackrest_repo.conf` e preencher `duora_backup_s3_cidrs`.
  A região fica nos EUA, como o servidor (Boston, [ADR-0026](../architecture/adr/0026-publico-idiomas-e-regiao-do-servidor.md)):
  os dados de pessoas no Brasil ficam nos EUA (fato); se isso é transferência internacional (LGPD art. 33) ou coleta internacional (Resolução CD/ANPD 19/2024, a confirmar) e qual salvaguarda se aplica é a confirmar (J-02)
  (spec 12; [data-map.md](../privacy/data-map.md#local-do-tratamento-e-transferências-a-confirmar)).
- **Proteção contra exclusão** (spec 18.3, ADR-0023): **pré-requisito de produção**, decidido (versionamento +
  Object Lock `COMPLIANCE` ≥ 21 dias, lifecycle de versões não correntes, credencial sem `DeleteObjectVersion`).
  Os comandos estão em [backblaze-setup.md](backblaze-setup.md); falta aplicá-los na conta. O verify já recusa um
  bucket sem isso e uma chave com poder demais.
- **Prova no host real**: rodar o playbook, conferir a chain `DUORA-BACKUP-EGRESS` (`iptables -S DOCKER-USER`), uma
  conexão de dentro do `backup` a um destino fora da allowlist falhando, restore completo com o volume real e o RTO
  cronometrado (spec 18.5), e o ensaio mensal de perda de VPS.
- **Restore isolado semanal automático** (spec 18.5) num ambiente de recuperação restrito, com a cópia e os segredos
  removidos depois.
- **Alertas** fora da VPS (tabela acima).
- **Backend** (passos 4 a 6 do restore): modo de recuperação com efeitos externos desligados, revogação em massa de
  sessões, registro externo de tombstones.

### Integração do upload com o Compose (issue #69)

`duora_media_review` no inventário gera `DUORA_MEDIA_REVIEW` no arquivo do host; o Compose atribui
`Media__Review: ${DUORA_MEDIA_REVIEW:-}` a web e worker. Ausente ou vazio mantém upload desligado.
Não defina `Media:Review` em appsettings padrão/Production, secrets, argumentos, env files de serviço ou outra
variável. O executor remove `DUORA_*` e `COMPOSE_*` herdados do shell antes de chamar Compose, para que prevaleça o
arquivo conferido pelo gate. O formato do interruptor é vazio ou nome sem aspas, expansão, comentário ou duplicata.
Além do formato, o `duora-deploy` só aceita um nome da allowlist `PRODUCTION_MEDIA_REVIEW_POLICIES`, as políticas que
o boot do web aceita em Production. Desde a decisão do dono de 28/10/2026
([ADR-0035](../architecture/adr/0035-revisao-humana-de-fotos.md#decisões-de-28102026)) ela é **`{"HumanReview"}`**:
revisão humana de fotos, sem aprovação automática, sem recurso e com um único revisor (o dono). `TechnicalOnly`
continua recusada em Production, e qualquer valor fora da allowlist (inclusive grafia diferente, como
`humanreview`) é recusado na leitura do `duora.env`, antes do pull e da migração, em vez de derrubar o web depois
dela. `Editorial` nos testes do executor é apenas um valor sintético posto na allowlist pelo próprio teste:
**não é uma política implementada**. Estar na allowlist **não liga o upload**: `duora_media_review` continua `""`
no inventário, e preenchê-lo com `HumanReview` é o passo de ativação da #69, depois do checklist abaixo.

**Quando houver outra política aceita em Production**, o mesmo PR precisa: acrescentá-la a
`PRODUCTION_MEDIA_REVIEW_POLICIES` (o teste confere que só entram nomes de `MediaReviewPolicy` e nunca
`TechnicalOnly`) e manter a política aceita pelo boot do Web e do Worker. O pipeline de mídia no Worker já foi
integrado pelo [PR #40](https://github.com/BipoXDXD/duora/pull/40):
[`WorkerHost`](../../backend/src/Duora.Worker/WorkerHost.cs) registra `AddMediaUploads`, `AddMediaProcessing`, o
sanitizador e `MediaProcessingService`. Sem `Media:Review`, o consumidor não processa a fila. Esse registro não
satisfaz o gate de isolamento do decoder: a sanitização ainda roda dentro do Worker, e o
[ADR-0025, item 9](../architecture/adr/0025-pipeline-de-fotos-imagesharp-s3-outbox.md) exige `media-sanitizer` sem
rede nem credenciais de storage, com limites de memória e CPU, antes de dados reais.

`DUORA_MEDIA_DIR` vem do mesmo `duora_media_dir` que o Ansible prepara (padrão `/var/lib/duora-media`). Os containers
usam a raiz fixa `/var/lib/duora-media`, com bind mounts separados, `create_host_path: false`, UID/GID `1654:1654` e
áreas `0700`. Web recebe quarentena RW e derivados RO; worker recebe ambas RW. Nenhum outro serviço monta fotos.
Os marcadores `.duora-media-area` continuam obrigatórios no boot do adaptador em Production, com o upload ligado ou
não, e o `duora-deploy` os confere no preflight, antes do pull e das migrations (adendo de 2026-10-19 do ADR-0025).
Instalar o Compose novo exige reaplicar `app_dirs`, inclusive o template do arquivo do host, antes da próxima release.

**Checklist antes de qualquer ativação em produção:**

1. Manter `duora_media_review: ""` até os demais gates do ADR-0025 e deste checklist estarem atendidos, inclusive
   o isolamento do decoder e a #86, e a ativação da #69 ser autorizada. A política permitida em Production já
   existe (`HumanReview`, decisão de 28/10/2026); o `duora-deploy` recusa qualquer valor fora da allowlist
   `{"HumanReview"}`. Não trocar o ambiente para Development nem a allowlist para contornar isso. Ao ligar, a
   revisão depende de um único revisor, o dono do projeto (papel `moderator` com as capacidades `assign`,
   `evidence` e `decide`, MFA e step-up na decisão); sem ele disponível, suspender novas admissões pelo
   procedimento aprovado, porque não há substituto nem recurso de fotos.
2. Executar e anexar evidência sanitizada do [passo 6 do setup B2](backblaze-setup.md#6-validar),
   em homologação e com dados sintéticos: provas negativas de chave/prefixo/File Lock, dois fulls, `expire`,
   `media-backup`/prune e comparação das versões antes/depois. Zero versões correntes apagadas, novos hide markers e
   nenhuma falha de autorização são condições de aprovação; um teste local com mocks não substitui essa prova.
3. Ensaiar restore dos derivados em staging conforme este runbook, reconciliando apenas assets Approved após
   tombstones (INV-11). Registrar release/digests, data, responsável e resultado; nunca anexar chaves ou fotos reais.
4. Conferir montagens efetivas: UID 1654 escreve/remove canário sintético na quarentena nos dois serviços; escreve
   derivados no worker; escrita/remoção de derivados pelo web falha com filesystem somente leitura. Conferir
   persistência após recriação, marcadores e recusa de inicialização com área/marcador ausente.
5. Com política suportada, ensaiar que timer parado, verify ausente/falho, futuro ou mais velho que 30 h barram o
   deploy antes de pull/stop, inclusive com `--accept-missing-backup-check`. Restaurar timer e rodar
   `duora-backup media-verify`; só então promover os mesmos digests pela revisão/gate de release.

A correção da configuração **não constitui evidência de homologação nem autoriza ativação**. O timer ativo e o
verify bom de até 30 h continuam obrigatórios; não atestam sozinhos o ensaio B2 ou uma política editorial pronta.
Para reverter a ativação, esvaziar o interruptor pelo inventário e fazer deploy aprovado; não apagar os diretórios,
marcadores ou backups. Nenhuma migração ou alteração de autorização/concorrência de API-M01–M04 acompanha este ajuste.
