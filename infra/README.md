# Infraestrutura

Release do Duora numa VPS Ubuntu Server 24.04 com Docker Engine + Compose e Caddy (spec 15). Procedimento de
deploy, manifesto e runbooks: [docs/operations/deploy.md](../docs/operations/deploy.md).

| Caminho | Conteúdo |
|---|---|
| `compose/compose.dev.yml` | PostgreSQL de desenvolvimento ([environment.md](../docs/development/environment.md)) |
| `compose/compose.prod.yml` | Produção e homologação: postgres, web, worker, edge e o job migrator |
| `images/dotnet/Dockerfile` | Alvos `web`, `worker` e `migrator` a partir do mesmo build |
| `images/edge/Dockerfile` | Caddy com a SPA e o Caddyfile embutidos |
| `images/postgres/` | PostgreSQL 18.6 + pgBackRest 2.59.3, configuração de WAL e do pgBackRest (lado postgres e lado repositório) e os scripts da 1a inicialização |
| `caddy/Caddyfile` | Borda: TLS, SPA, `/api`, `/hubs`, cabeçalhos de segurança e logs |
| `../docs/operations/backblaze-setup.md` | Bucket de backup e application keys no Backblaze B2, criados pelo responsável com o CLI `b2` (sem OpenTofu por enquanto, como o resto da infraestrutura) |
| `ansible/` | Configuração do host (roles `base`, `ssh`, `firewall`, `docker`, `app_dirs`, `backup`) |
| `../scripts/deploy/` | `duora-deploy`, `duora-secrets`, `duora-backup` e o smoke local (Python 3.12, só biblioteca padrão) |
| `../scripts/backup/` | Drill de restore PITR com MinIO de teste ([backup-restore.md](../docs/operations/backup-restore.md)) |

Sem OpenTofu por enquanto: provedor e arquitetura da VPS não foram escolhidos (spec 2.3, "não inventar provider").

## Imagens

Contexto de build = raiz do repositório, com `.dockerignore` em lista de permissão. Bases fixadas por tag **e**
digest do índice multiarquitetura; atualizar as duas juntas.

| Imagem | Base de execução | Usuário | Observações |
|---|---|---|---|
| web | `aspnet:10.0.13-noble-chiseled-extra` | 1654 | Sem shell nem gerenciador de pacotes; `-extra` traz ICU e tzdata |
| worker | `runtime:10.0.13-noble-chiseled-extra` | 1654 | Sem ASP.NET Core; nenhuma porta |
| migrator | `runtime-deps:10.0.13-noble` | 1654 | Bundle do EF Core self-contained + `migrate.sh` (lê o segredo) |
| edge | `caddy:2.11.7-alpine` | 10001 | Binário recompilado (xcaddy, Go 1.26.10, x/net 0.60.0); portas baixas por sysctl |
| postgres | `postgres:18.6-trixie` | 999 | pgBackRest do PGDG em versão exata; `pgbackrest version` roda no build; sem `gosu` |

- **Build sem emulação**: o SDK roda no `BUILDPLATFORM`. Web e worker saem como IL portátil (`UseAppHost=false`),
  idêntico nas duas arquiteturas; só o bundle de migração é nativo (gerado com o RID do alvo). Restore em
  `--locked-mode`, como no CI.
- **Scan das imagens** (spec 17.1): Trivy em cada imagem, nas duas arquiteturas; HIGH e CRITICAL com correção
  publicada bloqueiam. Achado sem correção (hoje só pacotes do Debian da base do postgres, status `affected`) não
  tem bump possível: sai num relatório no resumo do job e volta a bloquear quando a correção for publicada.
  Exceção pontual vai para o `.trivyignore`, com justificativa e validade.
- **amd64 e arm64**: o CI builda cada arquitetura no runner nativo (`ubuntu-24.04` e `ubuntu-24.04-arm`) e executa
  o bundle, o pgBackRest e o `caddy validate` (spec 2.4: emulação não é prova).
- **Sem `HEALTHCHECK` no web**: a imagem chiseled não tem shell nem cliente HTTP, e o Docker fora do Swarm não reinicia
  container `unhealthy`. A prontidão é verificada pelo deploy (`/health/ready` pela rede interna). O edge tem
  healthcheck do Compose (`wget` do Alpine num listener só de loopback).

## Compose de produção

- **Redes**: `edge` (Caddy + web), `data` (`internal: true`: postgres, web, worker, migrator), `egress` (worker,
  para storage, e-mail e verificação), `pgbackrest` (`internal: true`: postgres e backup) e `backup-egress`
  (`172.30.20.0/24`: só o backup e o job de restore, com saída filtrada no host). O postgres só está em redes
  `internal` e não tem `ports`; só o edge publica 80, 443 e 443/udp.
- **Backup** ([backup-restore.md](../docs/operations/backup-restore.md)): o container `backup` (mesma imagem do
  postgres) é o repository host do pgBackRest e o único com as credenciais do S3 e a senha de cifra; recebe o WAL do
  postgres por TLS com certificado de cliente, lê o PGDATA só leitura (sem o socket do banco) e fala SQL como
  `duora_backup` (sem superusuário, scram, só do IP `172.30.21.10`). O job `restore` (profile `restore`) monta o
  PGDATA gravável.
- **Autenticação do banco**: `pg_hba.duora.conf` versionado e sem `trust` (`postgres` só por `peer` no socket do
  próprio container; papéis de rede por `scram-sha-256`; o resto, `reject`).
- **Volumes nomeados**: `pgdata` em `/var/lib/postgresql` (layout do PG 18), `dataprotection`,
  `caddy_data`/`caddy_config`. O dono e o modo 0700 vêm da imagem na primeira montagem.
- **Endurecimento** (web, worker, edge, migrator): `read_only` + `tmpfs` em `/tmp`, `cap_drop: [ALL]`,
  `no-new-privileges`, usuário numérico não-root, limites de memória, CPU e PIDs (spec 15.1: web 1,5 GiB,
  worker 1 GiB, postgres 2,5 GiB, Caddy 128 MiB), logs com rotação (driver `local`, 5 × 10 MB).
- **PostgreSQL** roda direto como 999, também com `cap_drop: [ALL]`: o diretório do volume já é do usuário postgres
  na imagem, então o entrypoint não precisa de root para preparar o PGDATA. Ele não é `read_only` (PGDATA, socket e
  spool do pgBackRest). O `backup` tem o mesmo endurecimento de web e worker (`read_only`, `tmpfs` em `/tmp`).
- **Segredos** por Compose secrets (arquivos do host em `DUORA_SECRETS_DIR`). Fora do Swarm o Compose ignora
  `uid`/`gid`/`mode`, então cada arquivo fica 0400 com dono = UID do único container que o lê (`duora-secrets`).
  Web e worker leem `/run/secrets` com o provider KeyPerFile (`SecretsDirectory=/run/secrets`, nome do arquivo
  `ConnectionStrings__Duora`); `*_FILE` sozinho não funciona para o .NET (spec 15.4).
- **Banco**: `duora_migrator` é dono do banco e aplica DDL; `duora_app` (web e worker) só tem DML, via default
  privileges, sem `CREATE`, sem `TEMPORARY` e sem acesso ao histórico do EF (`reconcile-privileges.sql`, rodado
  pelo deploy depois de cada migração). Tabelas append-only perdem `UPDATE`/`DELETE` na própria migration
  ([model.md](../docs/data/model.md#permissões-de-banco-spec-107)). O superusuário `postgres` fica para
  administração.
- **Imagens por digest**: `DUORA_*_IMAGE` vêm do manifesto da release; sem tag mutável em produção.

## Caddy

Tabela 15.3 da spec: `/api/*`, `/hubs/*` e `/webhooks/*` (callbacks de fornecedor, corpo até 64 KiB, 413 na borda acima disso; ADR-0024) vão ao `duora-web:8080`, alias exclusivo da rede `edge` privada (hub com `flush_interval -1`, sem timeout de
requisição e `stream_close_delay 5m` em recarga de config); `/assets/*` é imutável por um ano e caminho inexistente
dá 404; `index.html` e demais estáticos com `no-cache`; o fallback da SPA só vale para navegação de documento
(`Sec-Fetch-Mode: navigate` ou `Accept: text/html`); `/health` e `/openapi` respondem 404 na borda.

CSP e `Permissions-Policy` em allowlist contra a matriz de capacidades (SPA, API, hub, fotos, áudio, voz ao vivo e
3D em tempo real), fixadas diretiva por diretiva contra o Caddy real por
`scripts/deploy/test_caddy_security_headers.py` em documento, assets, mídia, API, hub e erros:
`default-src 'none'`, só fontes próprias, o hash do `<style>` do `<model-viewer>` 4.4.0, `blob:` só em `connect-src`
(textura embutida no GLB), `worker-src 'none'` e Trusted Types (só a política `lit-html`); na `Permissions-Policy`,
`microphone`, `autoplay` e `speaker-selection` em `(self)` e toda outra feature reconhecida pelo Chromium em `()`.
Mais HSTS (1 ano, sem preload), `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, COOP/CORP
`same-origin` e sem `Server` nem `Via`, também nos erros gerados pela borda (`handle_errors`, `no-store`). O porquê de cada
valor, com fonte, e a medição do 3D estão em [deploy.md](../docs/operations/deploy.md#cabeçalhos-de-segurança-da-borda).

Logs de acesso em JSON no stdout sem query string, sem cabeçalhos de requisição e resposta (logo sem `Cookie`,
`Authorization` e `Set-Cookie`) e com IP truncado (/24 e /48) (spec 19.2).

## Ansible

```sh
cd infra/ansible
ansible-galaxy collection install -r requirements.yml
ansible-playbook -i ~/duora-ops/inventory.yml site.yml --check --diff
ansible-playbook -i ~/duora-ops/inventory.yml site.yml
```

O inventário real fica fora do git (copie `inventory/example.yml`). Lint e sintaxe sem instalar nada global:

```sh
uvx --from ansible-lint==26.10.1 --with ansible-core==2.21.5 ansible-lint
uvx --from ansible-core==2.21.5 ansible-playbook -i inventory/example.yml site.yml --syntax-check
```

- `base`: pacotes, UTC, `unattended-upgrades` com reinício automático às 04:30 UTC (`duora_unattended_reboot`).
  `/proc` com `hidepid=invisible`: quem não é root não vê processos (e linhas de comando) de outros usuários.
  O repositório do Docker fica fora das atualizações automáticas: atualizar o daemon é mudança planejada.
- `ssh`: drop-in `00-duora.conf` (só chave, sem root, sem senha, `AllowUsers`); recusa aplicar se o operador não
  tiver `authorized_keys`.
- `firewall`: UFW com IPv6, entrada negada por padrão, SSH com `limit` das origens permitidas, 80/443 TCP e 443 UDP.
- `docker`: repositório oficial; a chave é baixada fora de `/etc/apt/keyrings`, convertida para binário e só instalada se o arquivo final tiver exatamente uma chave primária com a impressão digital publicada (`files/validate_apt_key.py`, com teste; uma chave extra no arquivo também passaria a valer para o apt), `daemon.json` com `live-restore`,
  `no-new-privileges` e logs limitados. Ninguém entra no grupo `docker` (equivale a root).
- `backup`: segredos do backup (`duora-secrets init-backup`), `/usr/local/sbin/duora-backup`, timers
  systemd (full domingo 03:00 UTC, diferencial seg–sáb 03:00 UTC, `verify` a cada 15 min, `verify-repo` leve diário às
  05:00 UTC e `verify-repo-full` completo às quartas 06:00 UTC) e o filtro de saída `duora-backup-egress` (chain `DOCKER-USER` para `172.30.20.0/24`, aplicada antes do
  Docker e falhando fechado: DNS só para `duora_backup_dns_resolvers` ou os upstream do systemd-resolved, HTTPS
  restrito a `duora_backup_s3_cidrs` quando o provedor existir).
- `app_dirs`: `/opt/duora`, `/etc/duora` (0750), `/etc/duora/secrets` (0700), `/var/lib/duora` (0700),
  os comandos em `/usr/local/sbin`, os segredos gerados uma vez e o usuário `duora-deploy`: chave do CI presa a
  `sudo -n /usr/local/sbin/duora-deploy --manifest -` e sudoers só para esse comando (sem `sudo ALL`).

### Docker e UFW

Portas publicadas pelo Docker são tratadas na tabela `nat` antes das chains `INPUT` usadas pelo UFW, então **o UFW
não filtra container publicado** (D-INF-02). A política aqui é: (1) o UFW protege os processos do host (sshd);
(2) o `compose.prod.yml` publica só 80/443 do edge, e o smoke local confere que web, worker e postgres não publicam
nada; (3) o firewall do provedor repete a mesma política; (4) a prova é uma varredura externa autorizada depois de
cada mudança de rede (spec 15.2), não a leitura das regras.

## Decisões

| Decisão | Por quê | Custo |
|---|---|---|
| Um Dockerfile .NET com três alvos | Web, worker e migrator compartilham restore e build | Um arquivo maior |
| Chiseled `-extra` para web e worker | Sem shell e sem apt reduz a superfície; ICU/tzdata preservam globalização | Sem `HEALTHCHECK` nem depuração por shell no container |
| Caddy recompilado no estágio `caddy-build` | O binário oficial 2.11.7 traz Go 1.26.8 e x/net 0.59.0 com CVEs de DoS em HTTP/2 e TLS na borda | Um estágio a manter; volta ao binário oficial quando ele vier corrigido |
| Caddyfile e SPA dentro da imagem edge | A borda vira artefato imutável por digest, versionado com o frontend | Mudar a CSP exige nova imagem |
| Portas 80/443 por `ip_unprivileged_port_start` | Caddy sem root e sem capability, compatível com `no-new-privileges` | Sysctl por container no Compose |
| Papéis separados de migrador e aplicação | Spec 17.3; o app não altera schema | Rotação de senha manual em dois arquivos |
| Histórico do EF fechado ao app por reconciliação pós-migração | Vale desde a 1a migração e a cada deploy, sem tocar no backend nem copiar o DDL do EF para o initdb | Janela de segundos entre a migração e o `REVOKE` em que o app antigo ainda teria DML no histórico |
| Deploy e segredos em Python 3.12 (biblioteca padrão) | Já vem no Ubuntu 24.04 (o Ansible também exige), valida JSON sem `jq`, tem testes e checagem de tipos | Um interpretador a mais no caminho do deploy (o do sistema, atualizado pelo Ubuntu) |
| Backup por repository host `backup` (pgBackRest TLS), decidido pelo usuário em 2026-10-15 | O postgres fica só em redes `internal`, sem credencial do S3 nem senha de cifra; a saída para o repositório é de um único container filtrado no host | Um container a mais e CA/certificados próprios para rotacionar (825 dias) |
| Smoke por `/health/ready` de dentro do edge | Saúde nunca exposta na borda; usa o `wget` que o Alpine já tem | O smoke depende do edge estar de pé |

## Pendências

- **Backup**: provedor S3 com bucket versionado + Object Lock (pré-requisito de produção, ADR-0023), alertas fora
  da VPS e prova no host real
  ([backup-restore.md](../docs/operations/backup-restore.md#pendências-que-dependem-da-vps-ou-do-fornecedor)).
- **Registry (GHCR ou outro), push e manifesto pelo CI**: publicar as imagens multiarquitetura, gerar SBOM,
  assinatura/proveniência e o manifesto; dar ao host só leitura no registry; job de deploy com a chave dedicada.
- **OpenTelemetry Collector** local e destino externo de monitoramento, com orçamento de 256 MiB (spec 19).
- **Tudo que depende da VPS**: escolher provedor e arquitetura, OpenTofu para VPS/firewall/DNS, executar o playbook
  (idempotência ainda não provada em host real), varredura externa de portas, certificado ACME real, homologação.
- **Backend** (fora deste PR): Data Protection com nome de aplicação por ambiente (spec 11.2; o key ring já fica no
  volume, cifrado com o certificado do `duora-secrets`); Forwarded Headers
  confiando só no Caddy (spec 11.3; subnet da rede `edge` em `docs/operations/deploy.md`); recusa de boot com
  schema fora de `min..max`.
- **Dependabot** para as imagens em `infra/images/*` (área protegida, PR próprio).
- **Rebuild periódico** das imagens para pegar correções das bases, mesmo sem mudança de código.

## Agente operacional isolado (proposta)

Configuração separada em [`telemetry/`](telemetry/) e scripts em [`scripts/telemetry/`](../scripts/telemetry/).
O [runbook gateway/Slack](../docs/operations/telemetry.md) documenta instalação, permissões, alertas, atualização,
rollback e aceite real. Não altera o executor de deploy nem habilita serviços automaticamente.

## Build local protegido

`make image IMAGE=web` (também worker/migrator/edge/postgres) usa `scripts/docker-build.sh`:
resolve a arquitetura pelo daemon Docker ativo, força a saída nativa e inspeciona o resultado.
`make image-release IMAGE=web` exporta OCI com AMD64 e ARM64 para `.docker-artifacts/`, sem
carregar uma variante estrangeira no Docker local. Não muda o manifesto nem o executor de deploy.
Os testes de `scripts/deploy/test_docker_build.py` entram na descoberta existente do gate de infraestrutura.
O CI existente continua usando runners nativos de ambas as arquiteturas.

### Referências de release AMD64 + ARM64

Após os dois builds nativos, scans e drills, `Publish release images` reúne os
mesmos digests em índices do GHCR para `web`, `worker`, `migrator`, `edge` e
`postgres`. A tag de release é `<commit>-<run-id>-<attempt>`; ela resolve
`linux/amd64` e `linux/arm64` automaticamente. As tags com sufixo de arquitetura
são entradas intermediárias de proveniência. O deploy restrito continua recebendo
o artefato nativo por digest, sem rebuild nem mudança de schema.

O job `indexes` valida identidade de commit/CI/run, configurações reais das imagens
e os dois descritores aprovados antes e depois da publicação. A evidência fica no
artefato `release-multiarch-<attempt>`. Falta, duplicação ou troca de arquitetura
reprova a publicação completa e impede sua promoção pelo pipeline.
