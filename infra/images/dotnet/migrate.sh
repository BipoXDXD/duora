#!/bin/sh
# Aplica as migrations pendentes com o bundle do EF Core. Roda como job exclusivo (compose profile "migrate"),
# chamado pelo deploy antes de subir web e worker; a aplicação nunca migra no startup (spec 2.1 e 17.4).
# A connection string vem de arquivo (Compose secret), nunca de variável de ambiente ou do compose.
set -eu

secret_file="${DUORA_MIGRATOR_CONNECTION_FILE:-/run/secrets/duora_migrator_connection}"

if [ ! -r "$secret_file" ]; then
  echo "migrator: segredo ilegível ou ausente em $secret_file" >&2
  exit 64
fi

# O bundle só aceita a connection string por argumento; ela fica visível na lista de processos do host enquanto
# o job roda (pendência registrada em infra/README.md).
exec /app/efbundle --connection "$(cat "$secret_file")" "$@"
