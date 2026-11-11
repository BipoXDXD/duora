# shellcheck shell=bash
# Retry de comandos de rede no CI. Carregue com `source`; não execute.
#
# Os mirrors do runner hospedado às vezes falham em DNS ("Temporary failure resolving ...", apt-get exit 100)
# ou travam o download. Uma falha transitória não deve derrubar o job, mas a falha persistente continua fatal:
# a última tentativa devolve o código real do comando, então `set -e` do chamador interrompe o passo.
#
# CI_RETRY_DELAYS          esperas em segundos entre tentativas; padrão "15 45" (3 tentativas). Testes usam "0 0".
# CI_RETRY_ATTEMPT_TIMEOUT teto por tentativa em segundos, para comando externo que pode travar (o `timeout`
#                          não alcança função de shell). Sem valor, só o timeout-minutes do passo limita.

# retry_with_backoff <comando> [argumentos...]
retry_with_backoff() {
  local -a delays
  read -r -a delays <<< "${CI_RETRY_DELAYS:-15 45}"
  local attempts=$((${#delays[@]} + 1)) attempt=1 status

  while true; do
    status=0
    if [ -n "${CI_RETRY_ATTEMPT_TIMEOUT:-}" ] && ! declare -F "$1" > /dev/null; then
      timeout "$CI_RETRY_ATTEMPT_TIMEOUT" "$@" || status=$?
    else
      "$@" || status=$?
    fi
    if [ "$status" -eq 0 ]; then
      return 0
    fi
    if [ "$attempt" -ge "$attempts" ]; then
      echo "::error::'$*' falhou após $attempts tentativas (último código: $status)" >&2
      return "$status"
    fi
    echo "::warning::tentativa $attempt/$attempts de '$*' falhou (código $status); nova tentativa em ${delays[attempt - 1]}s" >&2
    sleep "${delays[attempt - 1]}"
    attempt=$((attempt + 1))
  done
}

# apt_retry <argumentos do apt-get...>: `sudo apt-get` com retries do próprio apt e timeout de rede por download,
# dentro do laço de tentativas acima. Opções do chamador (ex.: APT::Update::Error-Mode=any) vêm depois e prevalecem.
apt_retry() {
  retry_with_backoff sudo apt-get \
    -o Acquire::Retries=5 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 "$@"
}

# apt_configure_network: grava as mesmas opções em apt.conf.d, para ferramentas que chamam o apt por conta própria
# (ex.: `playwright install-deps`) e não deixam injetar `-o`.
apt_configure_network() {
  printf '%s\n' 'Acquire::Retries "5";' 'Acquire::http::Timeout "30";' 'Acquire::https::Timeout "30";' \
    | sudo tee /etc/apt/apt.conf.d/80-duora-network > /dev/null
}
