#!/usr/bin/env bash
# Primeira inicialização do volume: papéis e banco do Duora (spec 17.3: o migrador usa credencial própria).
#   duora_migrator  dono do banco "duora"; aplica as migrations (DDL).
#   duora_app       web e worker; só DML nas tabelas que o migrador criar, via default privileges. Sem DDL, sem
#                   CREATE em schema algum, sem tabela temporária. Exceções (docs/data/model.md, "Permissões"):
#                   o histórico de migrations perde tudo em reconcile-privileges.sql, que o deploy roda depois de
#                   cada migração; tabelas append-only perdem UPDATE/DELETE na própria migration que as cria.
#   duora_backup    pgBackRest no container backup, por TCP com scram-sha-256 e só da origem do backup
#                   (pg_hba.duora.conf). Não é superuser: lê configurações e executa as funções de backup e de WAL.
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
backup_password_file="${DUORA_BACKUP_PASSWORD_FILE:?defina DUORA_BACKUP_PASSWORD_FILE}"
require_secret_file "$migrator_password_file"
require_secret_file "$app_password_file"
require_secret_file "$backup_password_file"

# As senhas são lidas pelo próprio psql (\set com crase), sem passar pela linha de comando nem pelo ambiente; o psql
# remove a quebra de linha final e :'var' as cita como literal SQL.
psql -v ON_ERROR_STOP=1 --no-psqlrc --username "$POSTGRES_USER" --dbname postgres <<SQL
\set migrator_password \`cat ${migrator_password_file}\`
\set app_password \`cat ${app_password_file}\`
\set backup_password \`cat ${backup_password_file}\`

CREATE ROLE duora_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE duora_app LOGIN PASSWORD :'app_password';
CREATE ROLE duora_backup LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD :'backup_password';
\unset migrator_password
\unset app_password
\unset backup_password

-- Só o que o pgBackRest usa (backup, check e info): ler data_directory e afins, iniciar e terminar o backup,
-- trocar o segmento e criar o ponto de restauração do check. No banco postgres, onde ele conecta; sem CONNECT no
-- banco duora.
GRANT pg_read_all_settings TO duora_backup;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_backup_start(text, boolean) TO duora_backup;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_backup_stop(boolean) TO duora_backup;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_switch_wal() TO duora_backup;
GRANT EXECUTE ON FUNCTION pg_catalog.pg_create_restore_point(text) TO duora_backup;

CREATE DATABASE duora OWNER duora_migrator;
REVOKE ALL ON DATABASE duora FROM PUBLIC;
GRANT CONNECT ON DATABASE duora TO duora_app;

\connect duora
REVOKE ALL ON SCHEMA public FROM PUBLIC;
-- USAGE sem CREATE: o app enxerga os schemas do migrador, mas não cria objetos neles.
ALTER DEFAULT PRIVILEGES FOR ROLE duora_migrator GRANT USAGE ON SCHEMAS TO duora_app;
ALTER DEFAULT PRIVILEGES FOR ROLE duora_migrator GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO duora_app;
ALTER DEFAULT PRIVILEGES FOR ROLE duora_migrator GRANT USAGE, SELECT ON SEQUENCES TO duora_app;
-- EXECUTE vai a PUBLIC por padrão; aqui só o app executa as funções do migrador.
ALTER DEFAULT PRIVILEGES FOR ROLE duora_migrator REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE duora_migrator GRANT EXECUTE ON FUNCTIONS TO duora_app;
SQL
