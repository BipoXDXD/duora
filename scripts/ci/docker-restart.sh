# shellcheck shell=bash
# Restart do daemon Docker no runner hospedado depois do upgrade do docker-ce via apt. Carregue com `source`.
#
# O postinst do docker-ce já reinicia o serviço; o restart explícito que aplica o daemon.json novo estourava o
# StartLimitBurst do systemd e falhava com `start-limit-hit` (run 43260629565, #549). Por isso o contador de
# partidas é zerado antes do restart. Se o restart ainda falhar, o job mostra o diagnóstico, espera o containerd
# e tenta uma única vez mais; a segunda falha é fatal. Toda espera tem teto.
#
# DOCKER_RESTART_WAIT_SECONDS teto de cada espera (containerd ativo, daemon respondendo); padrão 60.

# docker_restart_after_upgrade: reinicia o docker.service e só retorna 0 com o daemon respondendo.
docker_restart_after_upgrade() {
  _docker_reset_start_limit
  if _docker_restart_and_wait; then
    return 0
  fi
  _docker_show_diagnostics
  echo "::warning title=Restart do Docker falhou; tentando uma segunda vez::diagnóstico no grupo acima;" \
    "se a segunda tentativa falhar, o job falha"
  _docker_reset_start_limit
  _docker_wait_until "containerd não ficou ativo" _docker_containerd_active || return 1
  echo "Segunda e última tentativa de reiniciar o docker"
  if ! _docker_restart_and_wait; then
    _docker_show_diagnostics
    return 1
  fi
}

# _docker_reset_start_limit: zera o contador de partidas e o estado `failed` das unidades.
# Best effort: com unidade em estado inesperado, quem decide é o restart logo depois.
_docker_reset_start_limit() {
  sudo systemctl reset-failed docker.service docker.socket || true
}

_docker_restart_and_wait() {
  sudo systemctl restart docker || return 1
  _docker_wait_until "daemon Docker não respondeu" _docker_daemon_answers
}

_docker_daemon_answers() {
  docker version --format '{{.Server.Version}}' > /dev/null 2>&1
}

_docker_containerd_active() {
  systemctl is-active --quiet containerd && sudo test -S /run/containerd/containerd.sock
}

# _docker_wait_until <mensagem de falha> <comando...>: sonda a cada 2 s até o teto.
_docker_wait_until() {
  local failure=$1 limit=${DOCKER_RESTART_WAIT_SECONDS:-60}
  shift
  local deadline=$((SECONDS + limit))
  until "$@"; do
    if [ "$SECONDS" -ge "$deadline" ]; then
      echo "$failure em $limit s"
      return 1
    fi
    sleep 2
  done
}

_docker_show_diagnostics() {
  echo "::group::docker.service não reiniciou: diagnóstico"
  sudo systemctl status docker docker.socket containerd --no-pager --lines=50 || true
  sudo journalctl -u docker -u docker.socket -u containerd --no-pager --since '5 min ago' | tail -n 200 || true
  echo "::endgroup::"
}
