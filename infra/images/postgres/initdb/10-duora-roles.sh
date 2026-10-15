#!/usr/bin/env bash
# Primeira inicialização do volume: papéis e banco do Duora (spec 17.3: o migrador usa credencial própria).
#   duora_migrator  dono do banco "duora"; aplica as migrations (DDL).
#   duora_app       web e worker; só DML nas tabelas que o migrador criar, via default privileges.
# O superusuário "postgres" fica só para administração. Rotação de senha: docs/operations/deploy.md.
set -euo pipefail

require_secret_file() {
  local file="$1"
  if [[ ! -s "$file" ]]; then
    echo "10-duora-roles: segredo ausente ou vazio em $file" >&2
    exit 1
  fi
}

migrator_password_file="${DUORA_MIGRATOR_PASSWORD_FILE:?defina DUORA_MIGRATOR_PASSWORD_FILE}"
app_password_file="${DUORA_APP_PASSWORD_FILE:?defina DUORA_APP_PASSWORD_FILE}"
require_secret_file "$migrator_password_file"
require_secret_file "$app_password_file"

# As senhas são lidas pelo próprio psql (\set com crase), sem passar pela linha de comando nem pelo ambiente; o psql
# remove a quebra de linha final e :'var' as cita como literal SQL.
psql -v ON_ERROR_STOP=1 --no-psqlrc --username "$POSTGRES_USER" --dbname postgres <<SQL
\set migrator_password \`cat ${migrator_password_file}\`
\set app_password \`cat ${app_password_file}\`

CREATE ROLE duora_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE duora_app LOGIN PASSWORD :'app_password';
\unset migrator_password
\unset app_password

CREATE DATABASE duora OWNER duora_migrator;
REVOKE ALL ON DATABASE duora FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE duora TO duora_app;

\connect duora
REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE duora_migrator GRANT USAGE ON SCHEMAS TO duora_app;
ALTER DEFAULT PRIVILEGES FOR ROLE duora_migrator GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO duora_app;
ALTER DEFAULT PRIVILEGES FOR ROLE duora_migrator GRANT USAGE, SELECT ON SEQUENCES TO duora_app;
SQL
