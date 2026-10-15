# Deploy na VPS

Procedimento de release do Duora na VPS Ubuntu Server 24.04 com Docker Engine + Compose (spec 15 e 17). Visão da
infraestrutura e decisões: [infra/README.md](../../infra/README.md).

**Não é zero-downtime.** Com uma instância web, o deploy tem uma breve interrupção planejada (spec 17.3): o worker
para antes da migração e web/edge são recriados. Escala horizontal exige o ADR de SignalR, sessões e fan-out.

## Peças

| Peça | Onde | Papel |
|---|---|---|
| `compose.prod.yml` | `/opt/duora/` (Ansible) | Serviços, redes, volumes, secrets e limites |
| `duora.env` | `/etc/duora/` (Ansible, 0640) | Configuração **não sensível** do host |
| Segredos | `/etc/duora/secrets/` (0700; arquivos 0400) | Senhas e connection strings, gerados uma vez |
| `duora-deploy` | `/usr/local/sbin/` (root, 0755) | Único comando que o executor de deploy pode rodar |
| `duora-secrets` | `/usr/local/sbin/` | Gera os segredos na primeira configuração |
| Estado do deploy | `/var/lib/duora/` (0700) | `current.json`, `previous.json`, `.deploy.lock` e `releases/<releaseId>.{env,json}` |

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
2. Backup/WAL: **ainda não verificável** (pgBackRest pendente). Sem `--accept-missing-backup-check` o deploy para;
   o comando liberado no sudoers não inclui essa flag, então produção fica bloqueada até a verificação existir.
3. Grava `<releaseId>.env` (digests) e `<releaseId>.json` em `/var/lib/duora/releases/` (subdiretório: um
   `releaseId` chamado `current` não sobrescreve o estado).
4. `pull` de todas as imagens por digest e conferência da arquitetura de cada uma.
5. Garante o PostgreSQL saudável (`up --wait postgres`) e lê a última migration aplicada, **antes de parar
   qualquer serviço**. Banco vazio ou atrás de `schema.max`: deploy normal. Banco à frente de `schema.max`: só
   segue como rollback elegível (abaixo); fora disso, recusa sem tocar em nada.
6. Para o worker (passo 4 da spec: sem novos leases durante a migração).
7. Roda o migrador como job exclusivo (`--profile migrate run --rm migrator`), aplica
   `reconcile-privileges.sql` (tira do `duora_app` todo acesso ao histórico do EF) e confere que a última migration
   do banco é a última do manifesto.
8. Sobe web, worker e edge da release (`up --wait --no-deps`).
9. Smoke: `GET http://web:8080/health/ready` de dentro do edge (rede interna), por até 90 s; depois confere que
   web, worker e edge estão `running` (pega restart em laço).
10. Só então grava `current.json` (e move o anterior para `previous.json`).

No rollback elegível os passos 6 e 7 não rodam: o banco fica como está.

A saída do migrador e dos comandos do Compose vai direto para a sessão do `duora-deploy` (no CI, o log do job); só o
que o deploy lê (arquitetura, migration, estado) é capturado.

Os passos 2 e 3 da spec (pausar rodadas e concluir salas) dependem de funcionalidades que ainda não existem.

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

Rollback é reimplantar o manifesto anterior (`/var/lib/duora/releases/<previousReleaseId>.json`) com o mesmo
comando. O `duora-deploy` só aceita um banco mais novo que o `schema.max` da release alvo quando ela é a **release
anterior elegível**: o `previousReleaseId` do manifesto corrente (`current.json`). Nesse caso o migrador não roda
e o schema fica como está, o que só é seguro porque as migrations seguem expand/contract (spec 17.4). Qualquer
outra release antiga é recusada antes de parar o worker. O migrador não desfaz migrations e nunca roda `Down`;
restaurar backup para desfazer deploy é decisão humana.

### Falha no meio do deploy

| Onde falhou | Estado | O que fazer |
|---|---|---|
| Validação, disco, pull, arquitetura | Nada mudou | Corrigir a causa e repetir |
| Migrador | Web anterior no ar, **worker parado** | A saída do migrador está na própria sessão do `duora-deploy` (o `run --rm` apaga o container e os logs junto); corrigir e repetir, ou religar o worker anterior com o env da release corrente |
| Readiness ou serviço fora de `running` | Release nova no ar, `current.json` ainda aponta a anterior | Logs do web/edge; rollback se o schema permitir |

### Primeira configuração do host

1. `ansible-playbook` (ver [infra/README.md](../../infra/README.md)) cria usuários, diretórios, configuração e
   segredos (`duora-secrets init /etc/duora/secrets`, que nunca sobrescreve).
2. O primeiro deploy cria os volumes; o PostgreSQL roda `10-duora-roles.sh` uma única vez e cria
   `duora_migrator` (dono do banco `duora`) e `duora_app` (só DML via default privileges; regras completas e
   convenção de tabela append-only em [model.md](../data/model.md#permissões-de-banco-spec-107)).

### Rotação de senha do banco

O script de inicialização só roda com o volume vazio. Para trocar a senha de `duora_app` (idem `duora_migrator`):
gerar a senha nova, `ALTER ROLE duora_app PASSWORD '...'` como `postgres`, gravar o arquivo `db_app_password` e a
connection string `duora_app_connection` com a mesma senha (0400, dono 1654 para a connection string e 999 para o
password) e recriar web e worker. Registrar no inventário de segredos.

## Validar o Caddyfile

```sh
docker run --rm -e DUORA_DOMAIN=app.example.com -e DUORA_TLS=ops@example.com \
  -v "$PWD/infra/caddy/Caddyfile:/etc/caddy/Caddyfile:ro" \
  caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f \
  caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
```

## Smoke local

Exercita o caminho inteiro numa máquina com Docker (portas 80 e 443 livres): registry descartável em
`127.0.0.1:5050`, cinco imagens publicadas para a arquitetura local, segredos de teste num diretório temporário,
`duora-deploy` de verdade com `tls internal`, verificações pela borda HTTPS e `down -v` no fim.

```sh
python3 scripts/deploy/smoke_local.py --commit "$(git rev-parse HEAD)"
```

`--keep` mantém o ambiente para inspeção; derrube depois com `docker compose -p duora down -v` e
`docker rm -f duora-smoke-registry`.

Testes dos scripts: `python3 -m unittest discover -s scripts/deploy -v`.
