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
- [ ] **Contato de suporte:** `Duora:SupportContact` traz um placeholder (`suporte@meetduora.com`) nas configurações
  base; a spec ainda não define o canal. Antes de abrir a participantes reais, troque pelo canal definido e confira
  que a caixa recebe.

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

1. Valida o manifesto e a configuração do host; confere a arquitetura do host e o espaço livre
   (`DUORA_MIN_FREE_GIB`, padrão 10 GiB, em `DUORA_DISK_CHECK_PATH`).
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
| Validação, disco, pull, arquitetura | Nada mudou | Corrigir a causa e repetir |
| Migrador | Web anterior no ar, **worker parado** | A saída do migrador está na própria sessão do `duora-deploy` (o `run --rm` apaga o container e os logs junto); corrigir e repetir, ou religar o worker anterior com o env da release corrente |
| Readiness ou serviço fora de `running` | Release nova no ar, `current.json` ainda aponta a anterior | Logs do web/edge; rollback se o schema permitir |

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
