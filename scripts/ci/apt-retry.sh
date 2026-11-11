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
#                          Estourado o teto, vai SIGTERM; se o processo não sair em 10 s, SIGKILL (`timeout -k 10`).
# CI_RETRY_RECOVER         nome de uma função chamada antes de cada nova tentativa (ex.: apt_recover_dpkg).

# retry_with_backoff <comando> [argumentos...]
retry_with_backoff() {
  local -a delays
  read -r -a delays <<< "${CI_RETRY_DELAYS:-15 45}"
  local attempts=$((${#delays[@]} + 1)) attempt=1 status

  while true; do
    status=0
    if [ -n "${CI_RETRY_ATTEMPT_TIMEOUT:-}" ] && ! declare -F "$1" > /dev/null; then
      timeout -k 10 "$CI_RETRY_ATTEMPT_TIMEOUT" "$@" || status=$?
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
    if [ -n "${CI_RETRY_RECOVER:-}" ]; then
      "$CI_RETRY_RECOVER"
    fi
    attempt=$((attempt + 1))
  done
}

# apt_recover_dpkg: conclui a configuração de pacotes que uma tentativa de install interrompida deixou pela metade.
# Best effort: se também falhar, a próxima tentativa do install é quem decide.
apt_recover_dpkg() {
  sudo dpkg --configure -a || true
}

# apt_use_fallback_mirror [diretório de fontes...]: troca os hosts *.ubuntu.com por espelhos fora desse domínio.
# O runner já passou horas sem resolver só *.ubuntu.com (o resto da rede respondia); repetir no mesmo host não
# adianta. As assinaturas do Release continuam conferidas pelo apt, então o espelho não ganha confiança extra.
# kernel.org não serve ubuntu-ports (arm64), que vai para a OCF/Berkeley. Idempotente; arquivo ausente é ignorado.
# CI_APT_MIRROR_FILES troca toda a lista padrão de arquivos do sistema (testes usam só arquivos temporários).
apt_use_fallback_mirror() {
  local -a candidates files=()
  if [ -n "${CI_APT_MIRROR_FILES:-}" ]; then
    read -r -a candidates <<< "$CI_APT_MIRROR_FILES"
  else
    candidates=(/etc/apt/apt-mirrors.txt /etc/apt/sources.list /etc/apt/sources.list.d/*.sources)
  fi
  local dir
  for dir in "$@"; do candidates+=("$dir"/*.sources "$dir"/*.list); done
  local file
  for file in "${candidates[@]}"; do [ -f "$file" ] && files+=("$file"); done
  [ "${#files[@]}" -gt 0 ] || return 0
  # ubuntu-ports primeiro: o padrão de /ubuntu também casaria com o começo de /ubuntu-ports.
  if sudo perl -pi -e 's#https?://[a-z0-9.-]*ubuntu\.com/ubuntu-ports#https://mirrors.ocf.berkeley.edu/ubuntu-ports#g;' \
    -e 's#https?://[a-z0-9.-]*ubuntu\.com/ubuntu#https://mirrors.edge.kernel.org/ubuntu#g' "${files[@]}"; then
    echo "::warning::apt: fontes *.ubuntu.com apontadas para os espelhos de fallback antes da nova tentativa" >&2
  else
    echo "::warning::apt: não consegui trocar o espelho; a nova tentativa usa as fontes atuais" >&2
  fi
}

# _apt_host_resolves <host>: o nome resolve em até 10 s. Separado para os testes trocarem por uma função falsa.
_apt_host_resolves() {
  timeout 10 getent hosts "$1" > /dev/null 2>&1
}

# apt_prefer_reachable_mirror [diretório de fontes...]: se o espelho do runner não resolve, troca antes da 1ª
# tentativa. Com Acquire::Retries=5, uma tentativa contra um nome que não resolve gastava ~11 min do prazo do job.
# CI_APT_PROBE_HOSTS troca os hosts sondados; o padrão segue a arquitetura (ports.ubuntu.com no arm64).
apt_prefer_reachable_mirror() {
  local -a hosts
  if [ -n "${CI_APT_PROBE_HOSTS:-}" ]; then
    read -r -a hosts <<< "$CI_APT_PROBE_HOSTS"
  elif [ "$(dpkg --print-architecture 2> /dev/null)" = arm64 ]; then
    hosts=(ports.ubuntu.com)
  else
    hosts=(azure.archive.ubuntu.com archive.ubuntu.com)
  fi
  local host
  for host in "${hosts[@]}"; do
    if ! _apt_host_resolves "$host"; then
      echo "::warning::apt: $host não resolve; a 1ª tentativa já usa o espelho de fallback" >&2
      apt_use_fallback_mirror "$@"
      return 0
    fi
  done
}

# Do apt_retry em curso: diretórios Dir::Etc::sourceparts e os pares `-o` do chamador. Vazios fora dele
# (ex.: playwright install-deps).
_apt_sourceparts=()
_apt_options=()

# apt_recover_install: antes de repetir um install, troca o espelho, refaz o update (as listas baixadas do host
# antigo não valem para as fontes novas, e o install responderia "Unable to locate package") e conclui o dpkg
# deixado pela metade. O update é best effort: se falhar, a próxima tentativa do install é quem decide.
# Com CI_RETRY_ATTEMPT_TIMEOUT, o update obedece ao mesmo teto das tentativas, porque o espelho também trava.
apt_recover_install() {
  local -a limit=()
  [ -z "${CI_RETRY_ATTEMPT_TIMEOUT:-}" ] || limit=(timeout -k 10 "$CI_RETRY_ATTEMPT_TIMEOUT")
  apt_use_fallback_mirror "${_apt_sourceparts[@]}"
  "${limit[@]}" sudo apt-get -o Acquire::Retries=5 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 \
    "${_apt_options[@]}" update -qq || true
  apt_recover_dpkg
}

# apt_recover_update: antes de repetir update ou remove, só troca o espelho (o dpkg não fica pela metade).
apt_recover_update() {
  apt_use_fallback_mirror "${_apt_sourceparts[@]}"
}

# apt_retry <argumentos do apt-get...>: `sudo apt-get` com retries do próprio apt e timeout de rede por download,
# dentro do laço de tentativas acima. Opções do chamador (ex.: APT::Update::Error-Mode=any) vêm depois e prevalecem.
# Antes de cada nova tentativa, o apt troca para o espelho de fallback, inclusive nas fontes do diretório passado em
# Dir::Etc::sourceparts; só o install também conclui o dpkg.
apt_retry() {
  local recover=apt_recover_update arg previous=""
  _apt_sourceparts=()
  _apt_options=()
  for arg in "$@"; do
    if [ "$previous" = -o ]; then
      _apt_options+=(-o "$arg")
      case "$arg" in Dir::Etc::sourceparts=*) _apt_sourceparts+=("${arg#Dir::Etc::sourceparts=}") ;; esac
    fi
    previous=$arg
  done
  case " $* " in
    *" install "*) recover=apt_recover_install ;;
  esac
  apt_prefer_reachable_mirror "${_apt_sourceparts[@]}"
  CI_RETRY_RECOVER="$recover" retry_with_backoff sudo apt-get \
    -o Acquire::Retries=5 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 "$@"
}

# apt_configure_network: grava as mesmas opções em apt.conf.d, para ferramentas que chamam o apt por conta própria
# (ex.: `playwright install-deps`) e não deixam injetar `-o`.
apt_configure_network() {
  printf '%s\n' 'Acquire::Retries "5";' 'Acquire::http::Timeout "30";' 'Acquire::https::Timeout "30";' \
    | sudo tee /etc/apt/apt.conf.d/80-duora-network > /dev/null
}
