#!/usr/bin/env bash
# Primeira inicialização do volume: inclui a configuração versionada do Duora (arquivamento de WAL) no
# postgresql.conf gerado pelo initdb. O include vale a partir do start definitivo, depois destes scripts.
set -euo pipefail

echo "include '/etc/postgresql/postgresql.duora.conf'" >> "$PGDATA/postgresql.conf"
