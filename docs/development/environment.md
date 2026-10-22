# Ambiente de desenvolvimento

Contrato do ambiente local (spec 2.4 e 2.5). Atualize este arquivo quando mudar ferramenta, arquitetura ou imagem.

## Versões verificadas

Inventário da máquina de desenvolvimento em 2026-10-15, lido com os comandos de diagnóstico da spec 2.5.

| Item | Versão encontrada | Fixada em |
|---|---|---|
| Sistema | macOS 27.0.1 (build 26A434), `arm64` nativo (sem Rosetta) | — |
| .NET SDK | 10.0.400 | `global.json` (`rollForward: latestPatch`) |
| Runtime .NET / ASP.NET Core | 10.0.12 | vem com o SDK |
| dotnet-ef | 10.0.13 | `.config/dotnet-tools.json` |
| Docker Engine (Docker Desktop) | 29.8.2, runtime `linux/arm64`, contexto `desktop-linux` | — |
| Docker Compose | 5.5.1 | — |
| PostgreSQL (container) | 18.6 | `infra/compose/compose.dev.yml` e `PostgresFixture.cs` |
| Node.js | 24.21.0 | `frontend/package.json` (`engines.node`: `>=24.0.0 <25`) |
| Corepack | 0.36.0 | vem com o Node.js |
| pnpm | 12.11.2 via `corepack pnpm` (sem pnpm global) | `frontend/package.json` (`packageManager`) |

O CI roda em Ubuntu 24.04; o inventário do runner fica registrado no workflow quando ele existir.

## Diagnóstico

```sh
scripts/doctor.sh
```

Só lê o ambiente. Termina com código 1 e diz o que fazer quando falta um pré-requisito (SDK compatível com o
`global.json`, `dotnet-ef` restaurado, Docker com runtime Linux, Compose, Node.js 24 e o pnpm do Corepack na versão
do `packageManager` quando `frontend/` existe).

## Primeira vez

```sh
dotnet tool restore                       # dotnet-ef na versão do manifesto
cd backend
dotnet restore --locked-mode              # falha se algum packages.lock.json divergir
```

O `nuget.config` da raiz limpa as fontes herdadas da máquina e mapeia todo pacote para nuget.org; fonte nova
exige mudar esse arquivo em revisão.

Depois de adicionar ou atualizar pacote em `Directory.Packages.props`, rode `dotnet restore` sem `--locked-mode`
para regenerar os lockfiles e versione-os junto.

## Build e testes

Todos os comandos a partir de `backend/`:

```sh
dotnet build Duora.slnx                   # avisos são erros (TreatWarningsAsErrors)
dotnet format Duora.slnx --verify-no-changes
dotnet test --solution Duora.slnx         # unit, arquitetura e integração
```

Os testes rodam na Microsoft Testing Platform (`global.json` → `test.runner`). Para um projeto só:
`dotnet test --project tests/Duora.UnitTests`.

A integração usa Testcontainers: o Docker precisa estar rodando, e cada execução sobe um PostgreSQL 18.6
descartável. Ela não usa o banco do Compose abaixo.

## HTTPS local

O Web tem dois perfis em `launchSettings.json`: `https` (`https://localhost:5001` e `http://localhost:5080`) e
`http` (só 5080). O Vite usa `https://localhost:5001` como padrão de `DUORA_API_ORIGIN`.

```sh
dotnet dev-certs https --trust            # uma vez por máquina; pede confirmação no Keychain
cd backend && dotnet run --project src/Duora.Web --launch-profile https
```

Em 2026-10-15 o certificado de desenvolvimento desta máquina existia, mas **não era confiável**: o
`dotnet dev-certs https --check --trust` saiu com código 7. Até rodar `--trust`, o navegador e o proxy do Vite
recusam o 5001.

## Frontend

O pnpm vem do Corepack, na versão de `packageManager`; se `corepack pnpm` não funcionar, rode
`corepack enable pnpm`. Os comandos abaixo rodam a partir de `frontend/`:

```sh
corepack pnpm install --frozen-lockfile   # falha se o pnpm-lock.yaml divergir
corepack pnpm cert:dev                    # exporta ~/.aspnet/https/duora-spa.{pem,key} (dotnet dev-certs --format Pem)
corepack pnpm dev                         # Vite em https://localhost:5173, com proxy de /api e /hubs para o Web
corepack pnpm typecheck
corepack pnpm lint
corepack pnpm test
corepack pnpm e2e                         # Playwright (antes, uma vez: corepack pnpm e2e:install)
```

### CSRF entre a SPA e o backend

Implementado no M1 ([auth.md](../security/auth.md#csrf)):
- `GET /api/v1/auth/csrf` emite o cookie legível `XSRF-TOKEN`; login e logout emitem um novo;
- a SPA devolve o valor no header `X-XSRF-TOKEN` em toda requisição que altera estado, inclusive login e logout;
- a origem `https://localhost:5173` é aceita somente no ambiente `Development`; falha dá `403`
  `CSRF_VALIDATION_FAILED`.

## Banco local (Compose)

A porta padrão é `55432`, porque a 5432 costuma estar ocupada por um PostgreSQL instalado no host. Ela só
escuta em `127.0.0.1`.

```sh
export DUORA_DB_PASSWORD='escolha-uma-senha-local'
docker compose -f infra/compose/compose.dev.yml up -d --wait     # sobe e espera o healthcheck
docker compose -f infra/compose/compose.dev.yml down             # para (mantém o volume)
docker compose -f infra/compose/compose.dev.yml down -v          # para e apaga os dados
```

`DUORA_DB_PORT` muda a porta. O volume nomeado monta `/var/lib/postgresql`, e a imagem 18 grava em
`/var/lib/postgresql/18/docker` (spec 15.4). Upgrade de major é um procedimento à parte, não troca de tag.

## Connection string da aplicação

Web e Worker compartilham o mesmo `UserSecretsId`, então um comando configura os dois. Os user-secrets só
valem no ambiente `Development` (o padrão do `dotnet run` pelos `launchSettings.json`) e não são um cofre.

```sh
cd backend
dotnet user-secrets set --project src/Duora.Web "ConnectionStrings:Duora" \
  "Host=127.0.0.1;Port=55432;Database=duora;Username=duora;Password=$DUORA_DB_PASSWORD"
```

Sem essa configuração os dois processos se recusam a subir e dizem o que falta.

## Estado local de desenvolvimento

O `appsettings.Development.json` do Web e do Worker aponta para `.local/` na raiz do repositório, que fica fora
do controle de versão. Os caminhos são relativos ao diretório de trabalho (o `dotnet run` usa o do shell), então
rode os dois a partir de `backend/`, como nos comandos deste arquivo.

| Configuração | Valor em Development | Para quê |
|---|---|---|
| `Duora:PublicOrigin` | `https://localhost:5173` | Links dos e-mails e origem aceita no CSRF |
| `DataProtection:KeysDirectory` (Web) | `../.local/dataprotection-keys` | Key ring; apagá-lo derruba as sessões abertas |
| `Email:PickupDirectory` (Worker) | `../.local/mail` | Cada e-mail de conta vira um `.eml` com o link de confirmação ou de redefinição |
| `Media:Review` (Web e Worker) | `TechnicalOnly` | Liga o upload de fotos com aprovação só técnica (dados sintéticos; o boot a recusa em Production, ADR-0025) |
| `Media:Storage:Provider` e `Media:Storage:FileSystemRoot` (Web e Worker) | `FileSystem`, `../.local/media` | Quarentena e derivados num diretório local, o mesmo nos dois processos; em Production, `FileSystem` num diretório absoluto já montado nos dois, no disco da VPS (adendo do ADR-0010); o adaptador `S3` existe, mas não é o de produção |

Sem essas chaves os processos não sobem, salvo as de `Media`: sem `Media:Review`, o upload de fotos fica
desligado (503), e o storage só deixa de ser exigido se `Media:Storage:Provider` também estiver ausente. Com o
provedor `FileSystem` em Production, como o `compose.prod.yml` sempre configura, o boot exige as duas áreas com o
marcador `.duora-media-area` mesmo com o upload desligado, e o `duora-deploy` confere o mesmo antes de migrar
(adendo de 2026-10-19 do ADR-0025). Fora de Development, defina-as por variável de ambiente. Atrás do Caddy,
defina também `ReverseProxy__KnownNetworks__0=172.30.10.0/24` (rede `edge`); sem ela, `X-Forwarded-For` é
ignorado e o rate limit por IP vê só o IP do proxy. Os e-mails só
saem com o Worker rodando: o Web grava na outbox e o Worker entrega.

## Outbox de e-mail

O worker valida `Outbox:SendTimeout ≤ Outbox:LeaseDuration / 2` no boot.
Padrões do e-mail: lote de 10, envio de 30 s, lease de 2 min e `MaxAttempts=8`. O lote não entra na conta:
antes de cada envio, o worker renova (heartbeat) o lease da entrega e das que ainda esperam no lote. A
verificação etária mantém suas opções nomeadas e seu lease de 2 min.

Se o heartbeat não renova a entrega (pausa maior que o lease, outro worker a assumiu), ela não é enviada e
volta no próximo claim.

No claim do e-mail, entregas elegíveis com tentativas esgotadas tornam-se `Failed`, com
`last_error=MaxAttemptsExceeded` e log `Error`, sem incrementar tentativas nem enviar; se o deadline já
venceu, tornam-se `Expired`. O payload é removido quando
não restam entregas abertas do evento. Inspecione status/erro sanitizado, nunca payloads ou tokens.

Não há migração. Rollback de código preserva estados terminais e dados, mas reintroduz as falhas
em entregas ainda abertas; prefira correção para frente. Nenhuma entrega `Failed` é reenviada automaticamente.

## Verificação etária simulada

Sem configuração, o fluxo etário fica desligado: `POST /api/v1/me/age-verifications` responde 503 e a conta continua
`PendingEligibility` ([ADR-0024](../architecture/adr/0024-fluxo-de-verificacao-etaria.md)). Para exercitá-lo
localmente, ligue o fornecedor simulado, só em Development (o app não sobe com `Fake` em outro ambiente). Web e
Worker compartilham o `UserSecretsId`, então os dois recebem a configuração:

```sh
cd backend
dotnet user-secrets set --project src/Duora.Web "AgeVerification:Provider" "Fake"
dotnet user-secrets set --project src/Duora.Web "AgeVerification:AllowedFlowOrigins:0" "https://age-check.fake.invalid"
dotnet user-secrets set --project src/Duora.Web "AgeVerification:Fake:SigningSecrets:0" "$(openssl rand -hex 32)"
```

O segredo assina os callbacks de `POST /webhooks/fake` (header `Duora-Fake-Signature: t=<unix>,v1=<hex>`, HMAC-SHA256
de `"{t}." + corpo`). Para rotacionar, configure o novo em `SigningSecrets:1`, troque no fornecedor e remova o
antigo.

| Configuração | Padrão | Para quê |
|---|---|---|
| `AgeVerification:Provider` | `None` | `None` ou `Fake` (Development e Testing) |
| `AgeVerification:PolicyVersion` | `synthetic-2026-10` | Versão gravada em cada solicitação (TM-37) |
| `AgeVerification:RequestLifetime` | `1.00:00:00` | Depois disso, uma solicitação sem resultado deixa pedir outra |
| `AgeVerification:AllowedFlowOrigins` | vazio | Origens aceitas para o fluxo do fornecedor; obrigatória com fornecedor |
| `AgeVerification:Fake:Mode` | `Succeed` | `Succeed`, `Slow`, `Fail` ou `DuplicateCallback` |
| `AgeVerification:Fake:SigningSecrets` | vazio | 1 ou 2 segredos HMAC (≥ 32 caracteres), obrigatórios com `Fake` |
| `AgeVerification:Dispatch:*` | como `Outbox:*`, com `SendTimeout` de 10 s | Lote, lease, timeout e retry do worker que abre no fornecedor |

O fluxo simulado aponta para `https://age-check.fake.invalid`, que não existe: localmente, a pessoa fica em
`ContinueWithProvider` até o resultado chegar pelo webhook.

## Migrations

A aplicação nunca migra no startup (spec 2.1). Em desenvolvimento:

```sh
cd backend
dotnet ef migrations add <Nome> --project src/Duora.Infrastructure --startup-project src/Duora.Infrastructure \
  --output-dir Persistence/Migrations
dotnet ef database update --project src/Duora.Infrastructure --startup-project src/Duora.Infrastructure \
  --connection "Host=127.0.0.1;Port=55432;Database=duora;Username=duora;Password=$DUORA_DB_PASSWORD"
```

O histórico fica em `ops.__ef_migrations_history`.

## Executar

```sh
cd backend
dotnet run --project src/Duora.Web        # perfil https: https://localhost:5001 e http://localhost:5080
dotnet run --project src/Duora.Worker
```

| Endpoint | Função |
|---|---|
| `GET /health/live` | Processo responde. Não consulta dependências. |
| `GET /health/ready` | Inclui o banco: 503 quando ele recusa a conexão ou não responde em 3 s. |
| `GET /openapi/v1.json` | Documento OpenAPI, só em `Development`. |

Transporte externo opt-in: [Resend e homologação descartável](../operations/resend.md). O desenvolvimento continua usando pickup por padrão.
