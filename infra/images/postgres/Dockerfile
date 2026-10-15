# PostgreSQL do Duora: imagem oficial fixada + pgBackRest instalado no mesmo ambiente do servidor (spec 18.2).
#   docker buildx build -f infra/images/postgres/Dockerfile --platform linux/arm64 .
# O archive_command do pgBackRest precisa enxergar o PGDATA e o socket; por isso ele mora nesta imagem, não num
# sidecar. A configuração do repositório (stanza, destino externo, criptografia) é pendência: infra/README.md.
#
# Mesma versão de PostgresFixture.cs e de compose.dev.yml (garantido por ComposeFileTests). Major nova não é troca
# de tag: é procedimento de upgrade de dados (spec 15.4).
FROM postgres:18.6-trixie@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336

# Versão exata do pacote do repositório PGDG (já configurado na imagem oficial). Se o PGDG retirar essa versão, o
# build falha de propósito: subir a versão é uma decisão revisada, não efeito colateral de rebuild.
ARG PGBACKREST_VERSION=2.59.3-1.pgdg13+1

RUN apt-get update \
 && apt-get install -y --no-install-recommends "pgbackrest=${PGBACKREST_VERSION}" \
 && rm -rf /var/lib/apt/lists/* \
 && pgbackrest version \
 && install -d -m 0755 /usr/local/share/duora \
 # gosu só serve para o entrypoint descer de root para postgres; a imagem roda como 999 (USER abaixo), então ele
 # nunca é chamado. Fica fora: o binário da imagem oficial traz um Go antigo com 25 CVEs HIGH/CRITICAL.
 && rm -f /usr/local/bin/gosu

LABEL org.opencontainers.image.title="duora-postgres" \
      org.opencontainers.image.source="https://github.com/BipoXDXD/duora" \
      io.duora.postgres.version="18.6" \
      io.duora.pgbackrest.version="${PGBACKREST_VERSION}"

# Roda só na primeira inicialização do volume (PGDATA vazio): cria os papéis do migrador e da aplicação.
COPY --chmod=755 infra/images/postgres/initdb/10-duora-roles.sh /docker-entrypoint-initdb.d/
# Rodado pelo duora-deploy depois de cada migração (versionado junto com a imagem da release).
COPY --chmod=644 infra/images/postgres/reconcile-privileges.sql /usr/local/share/duora/reconcile-privileges.sql

# Roda como o usuário postgres da imagem (999), como no compose.prod.yml: o volume em /var/lib/postgresql já é
# dele, então o entrypoint não precisa de root para preparar o PGDATA.
USER 999:999
