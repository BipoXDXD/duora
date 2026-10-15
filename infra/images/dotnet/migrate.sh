#!/bin/sh
# Aplica as migrations pendentes com o bundle do EF Core. Roda como job exclusivo (compose profile "migrate"),
# chamado pelo deploy antes de subir web e worker; a aplicação nunca migra no startup (spec 2.1 e 17.4).
# A connection string vem de arquivo (Compose secret): o DesignTimeDbContextFactory lê o caminho em
# DUORA_MIGRATOR_CONNECTION_FILE, então ela não aparece em argumento nem em variável de ambiente.
set -eu

DUORA_MIGRATOR_CONNECTION_FILE="${DUORA_MIGRATOR_CONNECTION_FILE:-/run/secrets/duora_migrator_connection}"
export DUORA_MIGRATOR_CONNECTION_FILE

if [ ! -r "$DUORA_MIGRATOR_CONNECTION_FILE" ]; then
  echo "migrator: segredo ilegível ou ausente em $DUORA_MIGRATOR_CONNECTION_FILE" >&2
  exit 64
fi

exec /app/efbundle "$@"
