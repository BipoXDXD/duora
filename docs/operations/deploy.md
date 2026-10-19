# Deploy na VPS

Procedimento de release do Duora na VPS Ubuntu Server 24.04 com Docker Engine + Compose (spec 15 e 17). Visão da
infraestrutura e decisões: [infra/README.md](../../infra/README.md). Backup e restore:
[backup-restore.md](backup-restore.md).

**Não é zero-downtime.** Com uma instância web, o deploy tem uma breve interrupção planejada (spec 17.3): o worker
para antes da migração e web/edge são recriados. Escala horizontal exige o ADR de SignalR, sessões e fan-out.

## Peças

| Peça | Onde | Papel |
|---|---|---|
| `compose.prod.yml` | `/opt/duora/` (Ansible) | Serviços, redes, volumes, secrets e limites |
| `duora.env` | `/etc/duora/` (Ansible, 0640) | Configuração **não sensível** do host |
| Segredos | `/etc/duora/secrets/` (0700; arquivos 0400) | Senhas, connection strings e o certificado do key ring do Data Protection, gerados uma vez |
| `duora-deploy` | `/usr/local/sbin/` (root, 0755) | Único comando que o executor de deploy pode rodar |
| `duora-secrets` | `/usr/local/sbin/` | Gera os segredos do banco (`init`) e os do backup (`init-backup`: certificados do pgBackRest e senha do `duora_backup`) na primeira configuração; troca e retira o certificado do key ring (`rotate-certificate`, `retire-previous-certificates`) |
| `duora-backup` | `/usr/local/sbin/` (root, 0755) | Wrapper restrito do pgBackRest: timers de backup e `verify` chamado pelo deploy ([backup-restore.md](backup-restore.md)) |
| Fotos | `/var/lib/duora-media/` (`duora_media_dir`, Ansible) | `quarantine/` e `derivatives/` (dono 1654, 0700), cada uma com o marcador `.duora-media-area`; montadas no web (derivados só leitura) e no worker. O web não sobe sem os marcadores, e o `duora-deploy` recusa antes do pull ([backup das fotos](backup-restore.md#backup-das-fotos-restic)) |
| Estado do deploy | `/var/lib/duora/` (root, 0700) | `current.json`, `previous.json`, `.deploy.lock`, `releases/<id>.{env,json}` (só releases implantadas com sucesso) e `staging/` (rascunho do deploy em curso) |

## Checklist da release

Antes de cortar o manifesto, confira os pré-requisitos de ordem entre releases (expand/contract,
[model.md](../data/model.md#convenções)). Cada item cita a consulta que prova que ele vale.

- [ ] **Avisos de credencial (TM-21):** o #52 (`feat/security-notifications`, gravação) não vai a deploy numa
  release que não tenha o #51 (`feat/security-notifications-reader`, leitor) já implantado. Confira que a release
  corrente (`current.json`) já contém o #51; antes de voltar para uma release anterior ao #51, a consulta de
  [rollback](#rollback-de-aplicação) dos tipos `...Notice` precisa devolver zero (rollback em dois passos, passando
  pela release do leitor).
- [ ] **Reset indisponível (bootstrap administrativo):** a gravação do `PasswordResetUnavailable` (bootstrap
  administrativo) não vai a deploy numa release que não tenha o leitor (`feat/admin-bootstrap-reader`) já
  implantado. Antes de voltar para uma release anterior a esse leitor, a consulta do
  [rollback](#rollback-de-aplicação) desse tipo precisa devolver zero.
- [ ] **Reaplicar `app_dirs`:** antes da primeira release com o Compose do #72, e sempre que `duora_media_dir` ou o
  template do `duora.env` mudarem, rode o playbook (`site.yml`, com `--check --diff` antes; [infra/README.md](../../infra/README.md)).
  Ele cria as áreas de fotos com os marcadores e grava `DUORA_MEDIA_DIR` no `duora.env`; sem isso o `duora-deploy`
  recusa no preflight. Confira com `sudo ls /var/lib/duora-media/{quarantine,derivatives}/.duora-media-area` e
  `sudo grep DUORA_MEDIA_DIR /etc/duora/duora.env`.
- [ ] **Rollback para antes do adendo do ADR-0010:** releases cujo web é anterior ao adendo de 2026-10-16 (fotos no
  disco da VPS) não sobem com o Compose atual; ver [rollback](#rollback-de-aplicação).
- [ ] **Contato de suporte:** `Duora:SupportContact` traz um placeholder (`suporte@meetduora.com`) nas configurações
  base; a spec ainda não define o canal. Antes de abrir a participantes reais, troque pelo canal definido e confira
  que a caixa recebe. O público principal não é brasileiro e o atendimento é em PT-BR e EN-US
  ([ADR-0026](../architecture/adr/0026-publico-idiomas-e-regiao-do-servidor.md), proposta 5): prefira um endereço
  neutro (`support@`) a `suporte@`.

## Manifesto da release (spec 17.2)

JSON, até 64 KiB, campos exatamente estes (campo desconhecido ou ausente é recusado):

```json
{
  "schemaVersion": 1,
  "releaseId": "2026.10.15-1",
  "commit": "06c3b683fbef3baaecd1ded6ebbe5db4e27a89c7",
  "createdAt": "2026-10-15T18:00:00Z",
  "architecture": "arm64",
  "images": {
    "web": "ghcr.io/bipoxdxd/duora-web@sha256:<64 hex>",
    "worker": "ghcr.io/bipoxdxd/duora-worker@sha256:<64 hex>",
    "edge": "ghcr.io/bipoxdxd/duora-edge@sha256:<64 hex>",
    "migrator": "ghcr.io/bipoxdxd/duora-migrator@sha256:<64 hex>",
    "postgres": "ghcr.io/bipoxdxd/duora-postgres@sha256:<64 hex>"
  },
  "contractVersion": "v1",
  "schema": { "min": "20261015175752_Baseline", "max": "20261015175752_Baseline" },
  "migrations": ["20261015175752_Baseline"],
  "sbom": { "web": "<referência>", "worker": "...", "edge": "...", "migrator": "...", "postgres": "..." },
  "gates": { "status": "passed", "runUrl": "https://github.com/BipoXDXD/duora/actions/runs/<id>" },
  "previousReleaseId": "2026.10.14-2"
}
```

| Campo | Regra verificada pelo `duora-deploy` |
|---|---|
| `releaseId` | `[0-9A-Za-z][0-9A-Za-z._-]{0,63}`: vira nome de arquivo, então sem `/` nem `..` |
| `commit` | SHA completo de 40 caracteres hexadecimais |
| `createdAt` | ISO 8601 em UTC |
| `architecture` | `amd64` ou `arm64`; igual à do host **e** à de cada imagem baixada |
| `images.*` | Só digest (`repo@sha256:...`), sem tag; prefixo igual a `DUORA_IMAGE_PREFIX` do host |
| `migrations` | Todas as migrations do bundle, em ordem; depois do migrador, a última precisa ser a do banco |
| `schema` | `min` é uma das migrations; `max` é a última. A recusa de boot com schema incompatível é pendência do backend |
| `gates.status` | Precisa ser `passed`; release sem gates aprovados não sobe |
| `sbom`, `runUrl` | Texto obrigatório (evidência); a verificação de assinatura/proveniência entra com o registry |
| `previousReleaseId` | Release de rollback elegível, ou `null` na primeira |

## O que o `duora-deploy` faz

Ordem da spec 17.3; qualquer falha interrompe com código 1 e mensagem acionável. Um deploy por vez: o comando
pega `flock` em `/var/lib/duora/.deploy.lock` antes de tudo e recusa na hora se outro estiver rodando (o kernel
solta o lock se o processo morrer).

1. Valida o manifesto e a configuração do host; confere a arquitetura do host, o espaço livre
   (`DUORA_MIN_FREE_GIB`, padrão 10 GiB, em `DUORA_DISK_CHECK_PATH`) e as áreas de fotos: `DUORA_MEDIA_DIR` absoluto,
   com `quarantine/.duora-media-area` e `derivatives/.duora-media-area` gravados pelo role `app_dirs`. O web os exige
   no boot mesmo com o upload desligado (adendo de 2026-10-19 do ADR-0025), então a recusa vem aqui, antes do pull.
2. Backup/WAL: verificado no passo 5, com o postgres e o backup da release já de pé e **antes de parar qualquer
   serviço**. `--accept-missing-backup-check` pula a verificação só no primeiro deploy (antes da stanza existir),
   em homologação ou no smoke; o comando liberado no sudoers não inclui essa flag.
3. Recusa um `releaseId` já registrado com outro conteúdo (releases são imutáveis) e grava `<releaseId>.env`
   (digests) e `<releaseId>.json` em `/var/lib/duora/staging/`. Nada em `staging/` decide coisa alguma; os arquivos
   só vão para `releases/` depois do smoke (passo 10).
4. `pull` de todas as imagens por digest e conferência da arquitetura de cada uma.
5. Garante o PostgreSQL e o `backup` (repository host do pgBackRest) saudáveis (`up --wait postgres backup`) e
   roda `/usr/local/sbin/duora-backup verify`: filtro de saída do `backup` no iptables, bucket com versionamento +
   Object Lock (ADR-0023), `pgbackrest check` (o segmento atual chega ao repositório),
   `pgbackrest info` sem erro, último WAL confirmado há no máximo 300 s (RPO), arquivamento sem falha pendente e
   último backup há no máximo 48 h. Falhou, o deploy para sem ter parado nada. Depois lê a última migration
   aplicada, **antes de parar qualquer serviço**. Banco vazio ou atrás de `schema.max`: deploy normal. Banco à frente de `schema.max`: só
   segue como rollback elegível (abaixo); fora disso, recusa sem tocar em nada.
6. Para o worker (passo 4 da spec: sem novos leases durante a migração).
7. Roda o migrador como job exclusivo (`--profile migrate run --rm migrator`), aplica
   `reconcile-privileges.sql` (tira do `duora_app` todo acesso ao histórico do EF) e confere que a última migration
   do banco é a última do manifesto.
8. Sobe web, worker e edge da release (`up --wait --no-deps`).
9. Smoke: `GET http://web:8080/health/ready` de dentro do edge (rede interna), por até 90 s; depois confere que
   web, worker e edge estão `running` (pega restart em laço).
10. Só então promove os arquivos para `releases/` e grava `current.json` (o anterior vira `previous.json`).

No rollback elegível os passos 6 e 7 não rodam: o banco fica como está.

A saída do migrador e dos comandos do Compose vai direto para a sessão do `duora-deploy` (no CI, o log do job); só o
que o deploy lê (arquitetura, migration, estado) é capturado.

Os passos 2 e 3 da spec (pausar rodadas e concluir salas) dependem de funcionalidades que ainda não existem.

## Versão nova com a SPA aberta

Quem está com a aba aberta durante um deploy continua na versão antiga até pedir um chunk que já não existe;
então a SPA recarrega uma vez (guarda de 60 s na sessão da aba) e, se ainda falhar, avisa que há versão nova e
pede para recarregar. Não há aviso proativo nem compatibilidade entre versões de chunks
([frontend/README.md](../../frontend/README.md#divisão-do-bundle-por-rota)).

## Orçamento de conexões (spec 10.7)

O PostgreSQL de produção tem `max_connections=100` (`compose.prod.yml`). Cada processo tem uma fatia fixa, com o
`Maximum Pool Size` do Npgsql explícito e validado no boot (`ConnectionShare`; `Database:MaxPoolSize` só pode
baixar a fatia, nunca passar dela). Valores iniciais (**hipótese**, a medir):

| Consumidor | Conexões | Onde |
|---|---|---|
| Web | 40 | `ConnectionShare.Web`; `Database:MaxPoolSize` de 1 a 40 |
| Worker | 10 | `ConnectionShare.Worker`; de 1 a 10 |
| Migrador (bundle) | 5 | `ConnectionShare.Migrator`, imposto sobre o arquivo de conexão |
| Sondas de saúde (readiness do web, heartbeat do worker) | 2 | `DatabaseProbe`, sem pool, uma por verificação |
| Backup (pgBackRest) e monitoramento | 5 | papéis próprios |
| Reserva do superusuário | 3 | `superuser_reserved_connections` padrão |
| **Soma** | **65** | sobram 35 para acesso administrativo e imprevistos |

O deploy para o web antigo antes de subir o novo (não há sobreposição de pools; ver o topo deste arquivo). Quem
espera conexão espera no máximo `Database:PoolTimeout` (**5 s**, de 1 a 30 s): pool esgotado, conexão recusada ou
servidor sem conexões (53300) respondem `503 TEMPORARILY_UNAVAILABLE` com `Retry-After`, nunca `500`
(`ServerErrorTests`). Transação esquecida aberta cai em 60 s (`idle_in_transaction_session_timeout`). Mudar uma
fatia exige refazer a soma aqui e o teste de boot correspondente (`StartupConfigurationTests`, `WorkerHostTests`).

## Proxy confiável (Forwarded Headers)

O Caddy é o único proxy. Ele fala com o web pela rede `edge`, que tem subnet fixa no `compose.prod.yml`:

| Rede | Subnet | Confiar em `X-Forwarded-*`? |
|---|---|---|
| `edge` | `172.30.10.0/24` | **Sim**: é o valor de `ForwardedHeadersOptions.KnownNetworks` no web (spec 11.3) |
| `data` | atribuída pelo Docker | **Não**: o worker e o migrador estão nela e alcançam o web; um cabeçalho vindo dali seria forjável |
| `egress` | atribuída pelo Docker | Não se aplica (o web não está nela) |
| `pgbackrest` | `172.30.21.0/24`, `internal` | Não se aplica: só postgres e backup (WAL por TLS; SQL do `duora_backup` só de `172.30.21.10`) |
| `backup-egress` | `172.30.20.0/24` | Não se aplica: só o backup e o job de restore; saída filtrada no host (`DOCKER-USER`) |

O postgres está só em redes `internal` (`data` e `pgbackrest`): não tem rota para fora nem porta publicada. O único
caminho até o repositório S3 é o container `backup`, cuja saída o filtro `duora-backup-egress` limita a DNS e HTTPS
(decidido pelo usuário em 2026-10-15; motivo e opções em [backup-restore.md](backup-restore.md#decisão-repository-host-via-tls-decidido-pelo-usuário-em-2026-10-15)).

Configurar o backend é pendência (fora deste PR): `KnownNetworks` só com `172.30.10.0/24`, `KnownProxies` vazio,
`ForwardLimit = 1`. Se a subnet colidir com uma rede do provedor, mude nos dois lugares no mesmo PR.

## Como o CI chama (quando houver VPS)

O executor tem uma chave SSH dedicada, presa no `authorized_keys` ao comando
`sudo -n /usr/local/sbin/duora-deploy --manifest -` (opção `restrict`: sem shell, túnel ou TTY). O manifesto vai
pelo stdin:

```sh
ssh -o StrictHostKeyChecking=yes -i chave-do-ci duora-deploy@<host> < manifest.json
```

A host key vem de fonte confiável (fora da primeira conexão) e fica num `known_hosts` do CI. Nunca
`StrictHostKeyChecking=no`.

## Operação manual

Sempre com os dois env files, para o Compose ver os mesmos digests da release corrente:

```sh
S=/var/lib/duora
ID=$(sudo python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["releaseId"])' $S/current.json)
sudo docker compose -f /opt/duora/compose.prod.yml --env-file /etc/duora/duora.env --env-file $S/releases/$ID.env ps
```

### Rollback de aplicação

Rollback é reimplantar o manifesto anterior (`/var/lib/duora/releases/<previousReleaseId>.json`, o mesmo
arquivo, sem editar) com o mesmo comando. Com o banco mais novo que o `schema.max` da release alvo, o
`duora-deploy` só segue se, **pelo estado que ele mesmo gravou** (nunca pelo manifesto recebido):

- a release alvo é o `previousReleaseId` da release corrente registrada em `releases/`;
- a release alvo já foi implantada aqui com exatamente esse manifesto;
- o banco não passa do `schema.max` da release corrente (teto: além disso o estado é desconhecido).

Nesse caso o migrador não roda e o schema fica como está, o que só é seguro porque as migrations seguem
expand/contract (spec 17.4). Qualquer outro caso é recusado antes de parar o worker, e nenhuma flag relaxa isso. O migrador não desfaz migrations e nunca roda `Down`;
restaurar backup para desfazer deploy é decisão humana.

**Releases anteriores ao adendo do ADR-0010** (fotos no disco da VPS, 2026-10-16). O `compose.prod.yml` é um só,
instalado pelo Ansible, e passa sempre `Media__Storage__Provider: FileSystem` ao web e ao worker. O web das releases
entre o upload de fotos (API-M01) e esse adendo recusa o filesystem em Production e não sobe: o deploy só falharia na
subida da aplicação, com o web corrente já substituído. Não faça rollback para uma delas com o Compose atual; se for
inevitável, é decisão humana com o Compose daquela época. Releases anteriores ao módulo de mídia ignoram as chaves
`Media__*` e só precisam que as áreas montadas existam.

O schema não é a única coisa que o rollback precisa ler: payloads da outbox gravados pela release nova também.
Campo novo em payload segue expand/contract ([model.md](../data/model.md#convenções)), com o leitor tolerante num
deploy e a gravação do campo no seguinte. **Restrição conhecida:** o campo `language` do pedido de e-mail de conta
(`identity.account-email-requested`, PR #22) entrou junto com a gravação. Um worker anterior a essa release lê o
payload com `UnmappedMemberHandling.Disallow` e encerra como `Failed` (`InvalidPayload`), sem retry, toda entrega que
tenha o campo. Não faça rollback para release anterior ao PR #22 enquanto houver entregas `transactional-email` em
`Pending` ou `InFlight`; se o rollback for necessário, drene a outbox antes (deixe o worker atual esvaziar a fila) e
confira:

```sql
select count(*) from ops.outbox_deliveries d
join ops.outbox_events e on e.id = d.outbox_event_id
where d.consumer = 'transactional-email' and d.status in ('Pending', 'InFlight') and e.payload ? 'language';
```

**Avisos de credencial (TM-21).** Os tipos `MfaEnabledNotice`, `MfaDisabledNotice`, `RecoveryCodesRegeneratedNotice`,
`PasswordChangedNotice` e `PasswordResetNotice` do mesmo evento seguem expand/contract em dois deploys: primeiro vai
o leitor (`feat/security-notifications-reader`, sem nenhuma gravação), depois a gravação
(`feat/security-notifications`). O instante citado no e-mail é o `occurred_at` do evento, não um campo do payload.
Um worker anterior ao leitor encerra esses tipos como `Failed` (`InvalidPayload`), sem retry: o aviso se perde.
Rollback da release com a gravação para a do leitor é seguro: o leitor entrega tudo o que a gravação grava
(`SecurityNoticeDispatchTests.EveryNoticeTheWriterRecordsIsDeliveredByTheReader`). Para uma release anterior ao
leitor, em dois passos, porque o web com a gravação continua gravando avisos enquanto estiver no ar:

1. volte primeiro para a release do leitor (sem gravação) e deixe o worker dela esvaziar a fila;
2. só com a consulta abaixo devolvendo zero, volte para a release anterior ao leitor. Entregas que ainda estejam em
   `Pending` por falha transitória (SMTP fora) contam: espere-as ou aceite perdê-las, e registre a decisão.

```sql
select count(*) from ops.outbox_deliveries d
join ops.outbox_events e on e.id = d.outbox_event_id
where d.consumer = 'transactional-email' and d.status in ('Pending', 'InFlight')
  and e.payload->>'kind' like '%Notice';
```

**Reset indisponível (bootstrap administrativo).** O tipo `PasswordResetUnavailable` do mesmo evento segue o mesmo
caminho: o leitor (`feat/admin-bootstrap-reader`, empilhado sobre o leitor dos avisos) vai a deploy sem nenhuma
gravação, e a gravação vem com o bootstrap administrativo, no deploy seguinte. A consulta acima não o cobre, porque
o nome não termina em `Notice`. Para voltar a uma release anterior ao leitor, só com esta fila vazia também:

```sql
select count(*) from ops.outbox_deliveries d
join ops.outbox_events e on e.id = d.outbox_event_id
where d.consumer = 'transactional-email' and d.status in ('Pending', 'InFlight')
  and e.payload->>'kind' = 'PasswordResetUnavailable';
```

**Bootstrap administrativo: rollback proibido com conta privilegiada sem MFA.** Uma release anterior ao bootstrap
(migration `AdminBootstrap`) não conhece `users.temporary_password_expires_at` nem o bloqueio do reset por e-mail
antes do MFA (I5). Lá, qualquer conta privilegiada sem MFA, ainda com a senha temporária ou já com a própria, seria
tomada por quem pedisse o reset com acesso à caixa dela, porque o reset da release antiga não depende da senha
atual: invalidar a senha não fecha o buraco. Por isso:

- o `duora-deploy` recusa, antes de parar qualquer serviço, o rollback que volta para antes de `AdminBootstrap`
  enquanto a consulta abaixo devolver mais que zero (`test_duora_deploy.py`,
  `test_rollback_across_the_bootstrap_with_privileged_account_without_mfa_is_refused_before_stopping_anything`);
- não há flag que relaxe a recusa. O caminho é **corrigir para frente**: uma release nova a partir da corrente, com
  a correção, implantada pelo fluxo normal. Se a falha for grave a ponto de exigir a release anterior, primeiro
  cada conta privilegiada ativa o MFA (ou perde o papel por decisão registrada do responsável técnico), e só então
  o rollback passa.

Conta privilegiada, para a guarda, é toda conta sem MFA com algum papel que não seja `participant`: um papel
novo entra contado sem mudar o script.

```sql
select count(*) from identity.users u where not u.two_factor_enabled and exists (
  select 1 from identity.user_roles ur join identity.roles r on r.id = ur.role_id
  where ur.user_id = u.id and r.name is distinct from 'participant');
```

O aviso de senha redefinida da reemissão usa o tipo `PasswordResetNotice`, que o leitor dos avisos já entrega; ele
não muda a ordem de deploy.

**Verificação etária sem validade** (migration `AgeVerificationWithoutExpiry`, ADR-0024 adendo de 2026-10-16): a
release nova grava `VerifiedAdult` sem `expires_at`. Um rollback para a release anterior funciona, mas falha
fechado para essas pessoas: o código anterior lê a verificação sem `expires_at` como vencida, nega a participação e
oferece nova verificação. Ninguém ganha acesso indevido; quem foi verificada depois do deploy refaz a verificação.

**Suspeita de menor durável** (migrations `AgeVerificationMinorSuspicion` e `AgeVerificationMinorSuspicionBackfill`,
ADR-0024): um rollback para release anterior a elas ignora `minor_suspected_at`, e quem tem suspeita gravada sobre
uma `VerifiedAdult` volta a participar se a conta estiver `Active`; antes de um rollback desses, confira
`select count(*) from identity.age_verifications v join identity.users u on u.id = v.user_id
where v.minor_suspected_at is not null and u.status = 'Active'` e restrinja essas contas antes, se houver.

**Sessão fora do pedido etário** (`identity.age-verification-requested`, ADR-0024 adendo de 2026-10-16): o campo
`loginSessionId` sai do payload em dois deploys, na ordem de remoção do [model.md](../data/model.md#convenções).
Nesta release o worker já não o lê (aceita presente ou ausente), mas o web **continua gravando**, porque o worker da
release anterior recusa o payload sem ele. A gravação sai na release seguinte; o campo some do leitor só depois que
nenhuma entrega gravada com ele puder estar `Pending` ou `InFlight`. Até a release que para de gravar, o rollback
para a anterior não tem restrição por esse campo.

### Falha no meio do deploy

| Onde falhou | Estado | O que fazer |
|---|---|---|
| Validação, disco, áreas de fotos, pull, arquitetura | Nada mudou | Corrigir a causa e repetir; área de fotos sem marcador: reaplicar `app_dirs` |
| Migrador | Web anterior no ar, **worker parado** | A saída do migrador está na própria sessão do `duora-deploy` (o `run --rm` apaga o container e os logs junto); corrigir e repetir, ou religar o worker anterior com o env da release corrente |
| Readiness ou serviço fora de `running` | Release nova no ar, `current.json` ainda aponta a anterior | Logs do web/edge; rollback se o schema permitir |
| Web recusa o boot sem marcador (`Media:Storage:FileSystemRoot em Production ...` no log) | Migrations aplicadas, web em restart. O worker não confere o marcador: só falha ao subir se a origem do bind mount (`$DUORA_MEDIA_DIR/quarantine` ou `derivatives`) não existir no host | O preflight conferiu os marcadores, então a área sumiu ou foi desmontada depois dele, ou o Compose instalado monta outro caminho que o `DUORA_MEDIA_DIR` do `duora.env`. Reaplicar `app_dirs`, conferir `docker compose ... config` e repetir o deploy; rollback não resolve, porque o Compose é o mesmo |

### Primeira configuração do host

0. Antes do host: o responsável cria no Backblaze B2 (US East) o bucket de backup com File Lock e as chaves
   ([backblaze-setup.md](backblaze-setup.md)); sem o bucket com versionamento e Object Lock, ou com a chave do backup
   com poder demais, o `duora-backup verify` falha e nenhum deploy de produção passa do passo 5. No inventário real
   entram `duora_b2_backup_bucket` e `duora_backup_s3_cidrs` (blocos IPv4 da Backblaze).
1. O `ansible-playbook` (ver [infra/README.md](../../infra/README.md)) cria usuários, diretórios, configuração,
   segredos do banco (`duora-secrets init`), segredos do backup (`duora-secrets init-backup`) e os timers de backup;
   nada disso sobrescreve o que já existe. Depois grave os segredos do repositório de backup
   (`pgbackrest_repo.conf` com `duora-secrets write-backup-repo`, `pgbackrest_cipher.conf`;
   [backup-restore.md](backup-restore.md#segredos-do-repositório-operador-uma-vez)) no `/etc/duora/secrets` que o
   playbook criou.
2. O primeiro deploy é do operador, com `--accept-missing-backup-check` (a stanza ainda não existe), seguido de
   `duora-backup stanza-create`, `full` e `verify` ([backup-restore.md](backup-restore.md#primeira-configuração)).
   Ele cria os volumes; o PostgreSQL roda `10-duora-roles.sh` e `20-duora-config.sh` uma única vez e cria
   `duora_migrator` (dono do banco `duora`) e `duora_app` (só DML via default privileges; regras completas e
   convenção de tabela append-only em [model.md](../data/model.md#permissões-de-banco-spec-107)).

3. Crie a primeira conta administrativa ([abaixo](#conta-administrativa-bootstrap-spec-111)).

### Conta administrativa (bootstrap, spec 11.1)

Conta privilegiada (`moderator`, `product_admin` ou `operator`) só nasce por este comando, rodado pelo operador no
host, **dentro do container do web em execução** da release corrente (mesma configuração, banco e key ring; nenhuma
rota da API concede papel). O primeiro administrador e os seguintes saem do mesmo jeito:

```sh
S=/var/lib/duora
ID=$(sudo python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["releaseId"])' $S/current.json)
sudo docker compose -f /opt/duora/compose.prod.yml --env-file /etc/duora/duora.env --env-file $S/releases/$ID.env \
  exec -T web dotnet Duora.Web.dll admin grant --email ana@exemplo.com.br --role product_admin --operator "$(logname)"
```

- **`exec -T`, nunca `run`.** A saída do `docker compose exec` volta só para o terminal de quem rodou; a de um
  container novo (`run`) passa pelo log driver do Docker, e a senha ficaria no `json-file` do host. O comando é um
  segundo processo no container do web, com uma ou duas conexões do banco durante segundos.
- A senha temporária é **gerada** pelo comando: nenhum argumento, variável de ambiente ou arquivo a recebe, então ela
  não aparece em `ps`, no histórico do shell nem no `docker inspect`.
- `--operator` é obrigatório e vai para a auditoria (`actor_kind = 'TerminalOperator'`, `actor_label` = o nome dado;
  ações `identity.admin_granted.<papel>` e `identity.admin_temporary_password_reissued`): use o seu usuário no host
  (`logname` devolve quem entrou por SSH, mesmo sob `sudo`). Formato de usuário de sistema: minúsculas, dígitos,
  `.`, `_` e `-`, até 32 caracteres. **É um rótulo, não uma identidade:** o Duora não o confere; quem responde pelo
  comando é quem tem SSH e `sudo` no host, e o log de autenticação do host é a prova.
- A saída mostra a **senha temporária uma única vez** (linha `Senha temporária: …`). O Duora guarda só o hash:
  não há como reexibi-la. Entregue-a à pessoa por um canal separado do e-mail dela e não cole em ticket, chat
  gravado ou arquivo. Ela vale **24 h**. Caixa e letras parecidas não importam ao digitá-la (`o`/`O` = `0`,
  `l`/`I` = `1`).
- **Onde a saída pode vazar fora do Duora**, e o que conferir antes de rodar:
  - log de I/O do `sudo` (`Defaults log_output` ou `log_input`/`log_output` no sudoers, gravado em
    `/var/log/sudo-io`): ele guarda a saída do terminal. Confira com `sudo -l` e o sudoers; se estiver ligado,
    rode a partir de uma sessão sem esse log ou trate o arquivo como segredo e apague a sessão depois;
  - `auditd` com `pam_tty_audit`: registra o que é digitado, não a saída, mas não cole a senha de volta no terminal;
  - gravação de terminal (`tmux` com log, `script`, `asciinema`, histórico de scrollback salvo pelo cliente SSH);
  - execução não interativa com log (Ansible, CI, cron, `ssh host comando` com a saída redirecionada para
    arquivo): não rode o comando assim. Ele é para um operador num terminal interativo.
- No primeiro login, a sessão só aceita trocar a senha (`POST /me/password-changes`; qualquer outra rota responde
  `403` `PASSWORD_CHANGE_REQUIRED`); a nova não pode repetir a temporária. Depois da troca, a sessão é a restrita
  do cadastro de MFA ([auth.md](../security/auth.md#mfa)), e só com o MFA ativo a conta abre `/admin`.
- Até a primeira ativação do MFA, a conta não redefine senha por e-mail (o pedido recebe um aviso sem link; I5).
- **Reemitir** (senha vencida ou perdida, ou a pessoa trocou a senha e não chegou ao MFA): o mesmo comando com
  `--reissue` e o mesmo `--role`. A senha atual e as sessões deixam de valer, a conta volta à troca de senha, e o
  titular recebe por e-mail o aviso de senha redefinida. Sem `--reissue`, o comando nunca mexe numa conta existente.
  Conta com MFA ativo não é reemitida: siga a recuperação de MFA de conta privilegiada (TM-22).
- O comando recusa (saída `1`) qualquer outra conta com o e-mail: participante, cadastro pendente, conta privilegiada
  com MFA ou de outro papel. Participante nunca vira conta privilegiada (D13); use outro e-mail. Argumento
  inválido sai com `2` e não toca o banco.
- **Cadastro pendente no caminho:** se alguém cadastrou o e-mail do futuro administrador pelo site e não confirmou,
  o comando recusa (o pendente pode ser de outra pessoa, e o comando não o substitui). Espere o prazo do pendente
  (72 h, hipótese) e a limpeza, ou use outro e-mail. Registre o caso: é um bloqueio barato de provocar.

### Certificado do key ring (spec 11.2)

O key ring do Data Protection (volume `dataprotection`) fica cifrado com um certificado X.509 autoassinado (RSA
3072, 10 anos), gerado pelo `duora-secrets init` em `dataprotection_certificate`: o certificado seguido da chave
privada PKCS#8, em PEM, 0400 e dono 1654. O web o recebe como `DataProtection__Certificate` e não sobe em
Production sem ele. `dataprotection_previous_certificates` (vazio fora de uma rotação) chega como
`DataProtection__PreviousCertificates` e só decifra chaves antigas. O framework nunca recifra uma chave: cada uma
continua precisando do certificado que a cifrou.

**Custódia separada.** Volume e certificado juntos abrem as sessões e os tokens. Logo depois do `init` e de cada
rotação, copiar `dataprotection_certificate` (e os anteriores, se houver) para o cofre do responsável técnico, fora
da VPS e fora do repositório de backup do banco e do volume (spec 18.3). Registrar no inventário de segredos a
impressão digital (`openssl x509 -noout -fingerprint -sha256 -in ...`), a data e o motivo. Restaurar o volume
sem o certificado equivale a perder o key ring: as sessões terminam em `401` e todo mundo entra de novo.

**Rotação** (planejada, nunca resposta a comprometimento):

1. `sudo duora-secrets rotate-certificate /etc/duora/secrets`: gera o certificado novo e põe o atual na frente de
   `dataprotection_previous_certificates`.
2. Copiar os dois arquivos para o cofre e recriar o web (`docker compose ... up -d --no-deps --force-recreate web`).
   As sessões seguem válidas: as chaves antigas são lidas com o certificado anterior, e as novas nascem cifradas
   com o novo.
3. Retirar os anteriores só quando nenhuma chave cifrada por eles for mais necessária. O prazo não é "90 dias
   depois da rotação": a chave padrão continua sendo a cifrada pelo certificado anterior até expirar, e a próxima
   pode já ter sido criada antes da rotação (o framework a cria até 2 dias antes), também com o anterior. Conte da
   maior `expirationDate` dessas chaves mais 24 h (token mais longo), o que pode passar de 92 dias depois da
   rotação. O comando confere isso no key ring
   e recusa enquanto houver chave assim:

   ```sh
   sudo duora-secrets retire-previous-certificates /etc/duora/secrets \
     --keys-directory /var/lib/docker/volumes/duora_dataprotection/_data
   ```

   Depois, recriar o web. Retirar cedo demais (à mão, sem o comando) derruba sessões e links com `401`/`400`,
   nunca `500` (`KeyRingCertificateTests`).

**Ligar o certificado num volume que já tem chaves:** as chaves existentes continuam em claro (o framework não
recifra) e seguem sendo usadas até expirar. Para a proteção valer de imediato, começar com key ring novo: volume
vazio, com a consequência de todas as sessões e links pendentes caírem (`401`/`400`).

**Comprometimento** do certificado ou do volume: não basta rotacionar. Gerar key ring novo, revogar todas as
sessões e invalidar tokens pendentes ([auth.md](../security/auth.md#data-protection)); a rotação acima só troca a
proteção em repouso.

### Licença da Six Labors no build do Worker (ADR-0025)

O ImageSharp 4 só compila em Release com a licença da Six Labors (chave community, gratuita no porte atual). Ela é
segredo de **build**, não de runtime: o Worker em execução não a lê.

| Pergunta | Resposta |
|---|---|
| Onde fica | Secret de repositório do GitHub Actions `SIXLABORS_LICENSE_KEY`. Também como secret do Dependabot com o mesmo nome, senão todo PR do Dependabot falha no job `images` (o Actions não entrega secrets de repositório a PRs do Dependabot). Nunca no repositório (`sixlabors.lic` está no `.gitignore` e no `.dockerignore`), em `.env`, em log ou na VPS |
| Quem custodia | O responsável técnico, que a obtém na conta da Six Labors. Agentes nunca a pedem nem a recebem |
| Como entra no build | `docker buildx build --secret id=sixlabors_license,env=SIXLABORS_LICENSE_KEY ...`: o `Dockerfile` monta o secret só no `RUN` do `dotnet publish` do Worker (`-p:SixLaborsLicenseFile=/run/secrets/sixlabors_license`), sem `ARG` nem `ENV`; o estágio final copia só `/out`. Sem o secret, o build da imagem para em `secret sixlabors_license: not found`; o `dotnet build` Debug local continua funcionando (só avisa) |
| Como se confere | Depois do build, `scripts/supply-chain/check_image_secret.py <imagem> SIXLABORS_LICENSE_KEY` procura a chave (e cada campo longo dela) no `docker history --no-trunc` e no filesystem exportado da imagem; achou, o job falha. O script nunca imprime a chave |
| Como rotacionar | Antes do `ExpiryDateUtc` da licença, ou se ela vazar: emitir uma licença nova na Six Labors, trocar o valor dos dois secrets (Actions e Dependabot), rodar o CI de um PR qualquer e conferir o job `images`. Imagens já publicadas não carregam a chave e continuam válidas. Se a licença vencer sem troca, só o build Release do Worker falha; o que está em produção segue rodando |

Para um build local da imagem do Worker, exporte a chave numa variável só da sessão do shell (lida do gerenciador de
senhas) e passe `--secret id=sixlabors_license,env=SIXLABORS_LICENSE_KEY`; nunca grave `sixlabors.lic` no clone.

### Storage de mídia fora do ar

Um upload em processamento tem 8 tentativas, com espera dobrando de 15 s até o teto de 15 min e jitter na metade de
cima: entre ~15 e ~31 min no total. Storage fora do ar por mais que isso rejeita esses uploads com
`PROCESSING_LIMIT`, apaga o original da quarentena e deixa a entrega `media-processing` em `Failed` com a causa; a
pessoa vê a foto recusada e reenvia. Não há reprocessamento manual: o original já não existe.

### Rotação de senha do banco

O script de inicialização só roda com o volume vazio. Para trocar a senha de `duora_app` (idem `duora_migrator`):
gerar a senha nova, `ALTER ROLE duora_app PASSWORD '...'` como `postgres`, gravar o arquivo `db_app_password` e a
connection string `duora_app_connection` com a mesma senha (0400, dono 1654 para a connection string e 999 para o
password) e recriar web e worker. Registrar no inventário de segredos.

## Validar o Caddyfile

```sh
docker run --rm -e DUORA_DOMAIN=meetduora.com -e DUORA_TLS=ops@example.com \
  -v "$PWD/infra/caddy/Caddyfile:/etc/caddy/Caddyfile:ro" \
  caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f \
  caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
```

O filtro dos logs (access log e log padrão, onde caem os erros de handler com o pedido inteiro) apaga cabeçalhos,
trunca IP, troca a query por `?redacted` e todo UUID do caminho por `{id}`, para rotas como
`/api/v1/me/blocks/{targetUserId}` não registrarem quem foi bloqueado. `scripts/deploy/test_caddy_access_log.py`
confere isso contra o Caddy real com UUID canário (roda no job `ops`; pula sem Docker), e o smoke local repete a
checagem na edge.

## Cabeçalhos de segurança da borda

A CSP e a `Permissions-Policy` vivem só no `infra/caddy/Caddyfile` e valem para toda resposta da borda: documento,
assets, API, hub, webhooks e os erros gerados pelo próprio Caddy (`handle_errors`, com `Cache-Control: no-store`).
As duas são **allowlist contra a matriz de capacidades** abaixo: cada fonte e cada feature liberada tem uma
capacidade que a usa, e o resto fica negado. A política esperada mora em `scripts/deploy/caddy_test_support.py`;
mudar o Caddyfile sem mudar o teste no mesmo PR falha o job `ops`, e a mudança exige revisão humana.

### Matriz de capacidades

| Capacidade | O que o navegador faz | CSP | Permissions-Policy |
|---|---|---|---|
| SPA (React 19, Vite) | módulos e CSS do bundle, sem inline nem eval | `script-src 'self'`, `style-src 'self'`, `font-src 'self'`, `img-src 'self'`, `manifest-src 'self'`; Trusted Types | nenhuma |
| API | `fetch()` na mesma origem | `connect-src 'self'` | nenhuma |
| Hub SignalR | WebSocket na mesma origem | `connect-src wss://<host>` | nenhuma |
| Fotos | envio por `fetch()` com o corpo cru ([ADR-0025](../architecture/adr/0025-pipeline-de-fotos-imagesharp-s3-outbox.md)); exibição por `<img>` lida pela API ([ADR-0010](../architecture/adr/0010-object-storage-externo-leitura-via-api.md)) | `connect-src 'self'`, `img-src 'self'` | nenhuma |
| Áudio pré-gerado | `<audio>` com `.m4a` e o fallback Opus (`.webm`/`.ogg`) de `/assets/`; Web Audio por `fetch()` + `decodeAudioData` | `media-src 'self'`, `connect-src 'self'` | `autoplay=(self)` |
| Voz ao vivo | `getUserMedia`; `<audio>.srcObject = MediaStream`; `RTCPeerConnection` com relay TURN; credencial por `POST /api/v1/rooms/{roomId}/voice/credentials` | nenhuma fonte nova: `srcObject` não passa pelo Fetch e ICE não passa por `connect-src` | `microphone=(self)`, `speaker-selection=(self)` |
| 3D em tempo real (`<model-viewer>` 4.4.0) | bundle próprio; um `<style>` no shadow root; GLB por `fetch()`; textura embutida por `fetch(blob:)`; WebGL; política `lit-html` do Lit | hash do `<style>`, `connect-src blob:`, `trusted-types lit-html` | nenhuma (sem `ar`: `xr-spatial-tracking` e sensores negados) |

Fora da matriz, e por isso negado: vídeo e câmera, compartilhamento de tela, tela cheia, AR, sensores, clipboard,
passkeys (spec 11.1 as deixa para depois; o PR delas libera `publickey-credentials-*`), pagamento, hardware
(USB, serial, HID, Bluetooth, MIDI, gamepad), geolocalização, worker e service worker, WebAssembly, iframe e
plugin. Pré-visualizar a foto antes do envio por `URL.createObjectURL` não existe hoje; se entrar, pede
`img-src blob:` no PR das fotos.

### CSP, diretiva por diretiva

| Diretiva | Valor | Por quê |
|---|---|---|
| `default-src` | `'none'` | Tudo que não está listado fica bloqueado, inclusive `frame-src` e `child-src`, logo nenhum iframe (CSP3 6.1.3) |
| `style-src` | `'self' 'sha256-PXtZ…OXOb0='` | O `<model-viewer>` põe um `<style>` no shadow root (`lib/template.js` da 4.4.0; o mesmo texto e o mesmo hash nos bundles de `dist/`). O hash libera só esse texto exato, sem `'unsafe-inline'` (CSP3 2.3.1 e 6.7.3.3, *hash-source*; 6.1.14 `style-src-elem`; MDN `style-src`). Subir o pacote exige recalcular o hash |
| `media-src` | `'self'` | `<audio src>` do catálogo e o fallback Opus, na mesma origem (CSP3 6.1.8). Sem `blob:` nem `data:` |
| `connect-src` | `'self' blob: wss://<host>` | API, credencial TURN, Web Audio e GLB por `fetch()`; hub por WebSocket. `blob:` só para a textura embutida no GLB: o `GLTFLoader` do three.js cria uma URL `blob:` para a imagem do *bufferView* e, com `createImageBitmap` (Chromium, Safari 17+, Firefox 98+), a lê por `fetch()`, que cai em `connect-src` (CSP3 6.1.2). `'self'` não casa `blob:` (CSP3 6.7.2.8). Uma URL `blob:` só lê bytes que a própria página criou: não abre destino de rede |
| `img-src` | `'self'` | `blob:` não é pedido: o `<img src="blob:">` do three.js só existe no `TextureLoader`, usado sem `createImageBitmap` (Safari < 17, Firefox < 98) |
| `worker-src` | `'none'` | Sem ela, `worker-src` cai em `script-src 'self'` (CSP3 6.2.2 e a lista de *fallback* de 6.8.3: `worker-src` → `child-src` → `script-src` → `default-src`), e qualquer script da origem poderia registrar worker ou service worker. Nada usa worker; os decodificadores Draco e KTX2 do three.js criam worker a partir de `blob:` e ficam fora do pipeline |
| `script-src` | `'self'` | Sem `'wasm-unsafe-eval'`: o pipeline do 3D não usa Draco, meshopt nem KTX2, os três decodificadores em WebAssembly |
| `require-trusted-types-for`, `trusted-types` | `'script'`, `lit-html` | Nenhum *sink* de HTML ou script (`innerHTML`, `new Function`, `script.src`, `new Worker`) aceita string (W3C Trusted Types; MDN `require-trusted-types-for`). O bundle do React só toca `innerHTML` nos caminhos de `<script>` em JSX e `dangerouslySetInnerHTML`, que a SPA não usa; o Zod roda em `jitless` (`frontend/src/app/zod-config.ts`) para não testar `new Function` em toda página. `lit-html` é a única política permitida, uma vez só, para o Lit do `<model-viewer>` |
| `base-uri`, `form-action`, `frame-ancestors`, `object-src` | `'none'`, `'self'`, `'none'`, `'none'` | Inalterados |
| `webrtc` | ausente | A CSP3 (6.2.1, 4.3.1) só bloqueia candidatos ICE com `webrtc 'block'`; `default-src` não a substitui. Ausente, a voz ao vivo funciona. A CSP não impõe relay-only nem restringe servidores ICE: isso é `iceTransportPolicy: "relay"` e a sinalização do [ADR-0028](../architecture/adr/0028-voz-ao-vivo-webrtc-relay-cloudflare-turn.md) |
| `upgrade-insecure-requests` | ausente | Ela reescreve URLs `http:` de subrecurso para `https:` (MDN). Aqui toda fonte é `'self'` em HTTPS com HSTS, e um `http:` é bloqueado em vez de reescrito em silêncio, o que mostra o defeito |
| `report-to`/`report-uri` | ausente | Não há coletor. Um endpoint de relatório é rota pública que recebe `POST` de qualquer navegador e precisaria de rate limit e teto próprios; entra quando houver monitoramento de CSP |

`Cross-Origin-Resource-Policy: same-origin` também vale para os assets. Ele só barra leitura *no-cors* por outra
origem (Fetch, "Cross-Origin-Resource-Policy header"): a SPA, o `<audio>`, o GLB e o `fetch()` são da mesma
origem e não são afetados, e outro site não embute nossos áudios e modelos. Os assets de mídia saem com o tipo
certo (`.m4a` → `audio/mp4`, `.ogg`/`.opus` → `audio/ogg`, `.webm` → `video/webm`, `.glb` → `model/gltf-binary`),
respondem `206` a `Range` e não são recomprimidos pelo `encode`; com outro tipo, o `nosniff` faria o navegador
recusar o arquivo.

O `header` da borda usa `defer`: os valores saem por cima dos do upstream, então o backend não consegue trocar nem
duplicar a CSP ou a `Permissions-Policy` (teste com upstream que manda as suas). Sem `defer` (e sem o `-Server`,
que também o liga), a resposta da API saía com duas CSPs. A borda também tira o `Server` e o `Via` que o
`reverse_proxy` acrescenta.

### Permissions-Policy

| Feature | Valor | Por quê |
|---|---|---|
| `microphone` | `(self)` | `getUserMedia` da voz ao vivo, só no documento da própria origem ([ADR-0028](../architecture/adr/0028-voz-ao-vivo-webrtc-relay-cloudflare-turn.md)). Não concede permissão nem impõe gesto: microfone desligado por padrão e pedido só depois do gesto são regras da SPA, inclusive com permissão já persistida. Frame de outra origem não o recebe, nem com `allow="microphone *"` |
| `autoplay` | `(self)` | No HTML Living Standard, um elemento de mídia só é *eligible for autoplay* se o documento tiver a feature: o `<audio autoplay>` que recebe a voz da outra pessoa começa a tocar quando ela fala, sem gesto novo. A feature não dispensa a ativação do usuário que o navegador exige para som; mudo por padrão continua regra da SPA (ADR-0027) |
| `speaker-selection` | `(self)` | Escolha da saída de áudio da voz ao vivo (fone ou alto-falante). A Audio Output Devices API (W3C) faz `selectAudioOutput()` e `setSinkId()` rejeitarem e esconde as saídas de `enumerateDevices()` quando a feature não é permitida. Liberado por decisão do responsável; nenhum código a usa ainda |
| as outras 65 | `()` | Toda feature que o Chromium reconhece numa página comum, conforme `permissions_policy_features.json5` (consultado em 2026-10-19); lista em `DENIED_FEATURES` |

Ficam fora da lista, de propósito:

- **`ch-*`** (dicas de cliente): controlam quais cabeçalhos `Sec-CH-*` o navegador envia, não APIs da página, e a
  CSP já não deixa a SPA falar com terceiros.
- **Só de Isolated Web App**: `all-screens-capture`, `controlled-frame`, `direct-sockets`,
  `direct-sockets-multicast`, `smart-card`, `sub-apps`, `usb-unrestricted` e `web-printing` não existem numa página
  comum.
- **Experimentais que, negadas, quebrariam a SPA**: `autofill` e `manual-text` (autofill e digitação nos
  formulários de conta), `focus-without-user-activation` (o `RouteAnnouncer` move o foco em toda navegação) e
  `vertical-scroll` (rolagem e `scrollIntoView`).

Feature que o navegador não conhece gera só um aviso no console (`Unrecognized feature` ou `Origin trial
controlled feature not enabled`) e passa a valer quando ele a reconhecer. No Chromium 156 são 13: `ambient-light-sensor`,
`device-attributes`, `execution-while-not-rendered`, `execution-while-out-of-viewport`, `haptics`,
`publickey-credentials-remote-client-data-json`, `rewriter`, `tools`, `unbounded-element`, `web-app-installation`,
`web-share`, `webnn` e `writer`. Só o Chromium lê o cabeçalho `Permissions-Policy` (MDN BCD: Firefox e Safari
não); neles valem o pedido de permissão do navegador e a CSP. A lista envelhece: a sonda de navegador afirma
que `document.featurePolicy.allowedFeatures()` é exatamente `autoplay`, `microphone` e `speaker-selection`, então
uma feature nova do Chromium com padrão `self` a faz falhar, quando ela roda, e pede a revisão da lista.

### 3D em tempo real: o que foi medido

Com o `<model-viewer>` 4.4.0 (three.js 0.183.2, a versão do `peerDependencies`) empacotado pelo Vite 8.3.4 e
servido por este Caddyfile, nos motores do Playwright 1.64.1 (Chromium 156, WebKit e Firefox), em 2026-10-19:

- com a CSP anterior, nos três: o `<style>` do shadow root bloqueado (`style-src-elem`), a textura embutida
  recusada (`connect-src blob:`; `THREE.GLTFLoader: Couldn't load texture blob:`) e um `WebAssembly.instantiate`
  bloqueado ao carregar o pacote;
- com esta CSP, GLB com PNG embutido, GLB com WebP (`EXT_texture_webp`) e GLB com textura externa carregam com
  textura, nos três motores;
- o `WebAssembly.instantiate` que sobra vem de `three/examples/jsm/libs/meshopt_decoder.module.js`, que instancia
  o decodificador ao ser importado, mesmo sem modelo meshopt. Com um `resolve.alias` do Vite apontando esse módulo
  para um stub (`MeshoptDecoder` com `supported: false`), não sobra violação nenhuma nos três motores.

O pipeline do 3D, então: GLB autocontido, texturas WebP via `EXT_texture_webp` (ou PNG/JPEG), sem
`KHR_draco_mesh_compression`, `EXT_meshopt_compression` nem `KHR_texture_basisu`, e o alias do meshopt no
`vite.config.ts`. A medição foi feita fora do repositório, porque o `<model-viewer>` ainda não é dependência; o
PR da primeira experiência com 3D traz o teste no navegador (ver ADR-0027, seção 7).

### Testes

`scripts/deploy/test_caddy_security_headers.py` (job `ops`; pula sem Docker) sobe o Caddyfile na imagem da edge,
com um site sintético (HTML, JavaScript, `.m4a`, `.ogg`, `.opus`, `.webm` e `.glb`) e um upstream sintético no
lugar do web, e fala HTTPS com ele pela porta publicada só no loopback, confiando só na CA local do Caddy. Afirma:

- CSP e `Permissions-Policy` exatas e únicas em documento, rota da SPA, assets, mídia, 404 de asset e de rota,
  `/health` e `/openapi`, API (200, 401, 403, 500), hub (200, 401, 404), webhook e 502 com o upstream parado;
- nenhuma fonte curinga, `'unsafe-*'`, `'wasm-unsafe-eval'`, esquema genérico, `http:` ou esquema local, exceto
  `blob:` em `connect-src`; nenhuma diretiva `webrtc`;
- só `autoplay`, `microphone` e `speaker-selection` liberados, e só para `self`;
- upstream que manda CSP e `Permissions-Policy` próprias não troca nem duplica as da borda;
- erros da borda com `no-store`; mídia com o tipo certo, cache imutável, sem `Content-Encoding` e `206` a `Range`.

Com `DUORA_TEST_BROWSER=1` roda também `scripts/deploy/test_caddy_browser.cjs` no Chromium do Playwright do
frontend (dependências do frontend instaladas e `pnpm exec playwright install chromium`):

```sh
DUORA_TEST_BROWSER=1 python3 -m unittest discover -s scripts/deploy -p test_caddy_security_headers.py -v
```

A sonda confere, com captura sintética e sem TURN: mídia e Web Audio da própria origem; mídia de outra origem,
`data:` e `blob:` bloqueadas; `fetch` e WebSocket de outra origem, estilo inline, WebAssembly, worker e iframe
bloqueados; `fetch(blob:)` permitido; Trusted Types recusando string em `innerHTML` e qualquer política além de
uma `lit-html`; as features permitidas exatas; tela cheia e câmera negadas pela política; microfone negado e
concedido sem gesto exigido pela política; `MediaStream` no `<audio>` sem fonte na CSP; ICE coletado apesar de
`default-src 'none'`; frame de outra origem sem microfone mesmo com `allow`. A sonda não roda no CI: o job `ops`
não tem navegador, e acrescentá-lo é mudança de workflow, em PR próprio. O e2e do frontend roda no `vite
preview`, sem a CSP da borda.

Fontes: W3C CSP Level 3 (Editor's Draft: 2.3.1, 4.3, 6.1.2, 6.1.3, 6.1.8, 6.1.14, 6.2.1, 6.2.2, 6.7.2.8, 6.7.3.3 e 6.8.3); W3C Trusted
Types; W3C Permissions Policy e a lista de features do repositório `w3c/webappsec-permissions-policy`;
`permissions_policy_features.json5` do Chromium; W3C Audio Output Devices API (`speaker-selection`); HTML Living
Standard (*eligible for autoplay* e algoritmo de carga do elemento de mídia, modo `object` sem Fetch); Fetch Standard
(`Cross-Origin-Resource-Policy`); MDN `Permissions-Policy` e browser-compat-data, `style-src`, `worker-src`,
`upgrade-insecure-requests`, `require-trusted-types-for` e `trusted-types`; código do `@google/model-viewer`
4.4.0 (`lib/template.js`, `three-components/CachingGLTFLoader.js`) e do three.js 0.183.2 (`GLTFLoader.js`,
`ImageBitmapLoader.js`, `meshopt_decoder.module.js`); Zod 4 (`z.config({ jitless: true })`).

## Smoke local

Exercita o caminho inteiro numa máquina com Docker (portas 80 e 443 livres): registry descartável em
`127.0.0.1:5050`, cinco imagens publicadas para a arquitetura local, segredos de teste num diretório temporário,
`duora-deploy` de verdade com `tls internal`, verificações pela borda HTTPS, recriação do container web conferindo
que o key ring do Data Protection sobrevive no volume (T-15) e `down -v` no fim.

```sh
python3 scripts/deploy/smoke_local.py --commit "$(git rev-parse HEAD)"
```

`--keep` mantém o ambiente para inspeção; derrube depois com `docker compose -p duora down -v` e
`docker rm -f duora-smoke-registry`.

Testes dos scripts: `python3 -m unittest discover -s scripts/deploy -v`.
