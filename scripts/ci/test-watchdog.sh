#!/usr/bin/env bash
# Watchdog dos testes do backend (#507). Uso: test-watchdog.sh ATRASO_SEGUNDOS DIRETORIO_SAIDA
#
# Depois do atraso, tira um dump com heap de cada host de teste ainda vivo (processos Duora.<Algo>Tests), antes que
# o timeout do job mate tudo sem deixar pilha. O hangdump do Microsoft.Testing.Platform não serve aqui: o timer dele
# zera a cada teste concluído, então um teste preso enquanto os outros terminam nunca dispara (medido no #507).
#
# Nunca reprova o passo: dump ausente ou falho vira aviso. O runner do GitHub tem ptrace_scope=1 e o watchdog não é
# ancestral do host de teste, por isso o createdump roda com sudo -n fora dos testes (DUORA_CREATEDUMP substitui).
set -uo pipefail

delay=$1
out=$2
test_hosts='(^|/)Duora\.[A-Za-z]+Tests(\.dll)?( |$)'

default_createdump() {
  local runtime
  runtime=$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" { dir = $3 "/" $2 } END { print dir }')
  runtime=${runtime#[}
  runtime=${runtime/]\//\/}
  echo "sudo -n $runtime/createdump"
}

sleep "$delay"

pids=$(pgrep -f "$test_hosts" | grep -vx "$$" || true)
if [ -z "$pids" ]; then
  echo "watchdog: nenhum host de teste vivo após ${delay}s"
  exit 0
fi

read -r -a createdump <<< "${DUORA_CREATEDUMP:-$(default_createdump)}"
for pid in $pids; do
  echo "watchdog: dump do host de teste $pid após ${delay}s"
  if ! "${createdump[@]}" --withheap --name "$out/%e_%p_watchdog.dmp" "$pid"; then
    echo "watchdog: aviso: createdump falhou para $pid"
  fi
done
# Com sudo, o dump nasce do root; o upload do artifact roda como o usuário do runner.
if [ "${createdump[0]}" = sudo ]; then
  sudo -n chmod a+r "$out"/*_watchdog.dmp 2>/dev/null || echo "watchdog: aviso: chmod do dump falhou"
fi
exit 0
