#!/bin/sh
# Fresh reviewed files only. No credential generation, firewall changes, enabling or starting services.
set -eu
[ "$(id -u)" = 0 ] || { echo 'Administrator required.' >&2; exit 1; }
[ "$#" = 2 ] && [ "$2" = --approve-install ] || { echo 'Required: source|gateway --approve-install' >&2; exit 1; }
ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/../.." && pwd)
[ ! -e /etc/duora-telemetry ] && [ ! -e /opt/duora-agent ] && [ ! -e /opt/duora-telemetry ] || {
  echo 'Existing integration: use the reviewed migration/rollback procedure.' >&2; exit 1;
}
case "$1" in
  source)
    python3 "$ROOT/scripts/openclaw/capacity.py" --profile source
    if getent passwd duora-shipper >/dev/null; then
      echo 'Reserved shipper account already exists.' >&2; exit 1
    fi
    useradd --system --user-group --no-create-home --shell /usr/sbin/nologin duora-shipper
    install -d -m 0755 /opt/duora-telemetry /etc/duora-telemetry
    install -m 0644 "$ROOT"/scripts/openclaw/*.py /opt/duora-telemetry/
    install -m 0644 "$ROOT/infra/openclaw/remote/sender.example.json" /etc/duora-telemetry/sender.json
    install -m 0644 "$ROOT"/infra/openclaw/remote/source/* /etc/systemd/system/
    ;;
  gateway)
    if getent passwd duora-receiver >/dev/null || getent passwd duora-monitor >/dev/null; then
      echo 'Reserved operations account already exists.' >&2; exit 1
    fi
    sh "$ROOT/scripts/openclaw/install.sh" --approve-install --remote-gateway
    useradd --system --user-group --no-create-home --shell /usr/sbin/nologin duora-receiver
    install -d -m 0755 /etc/duora-telemetry /etc/systemd/system/duora-observer.service.d /etc/systemd/system/duora-monitor.service.d
    install -m 0644 "$ROOT/infra/openclaw/remote/receiver.example.json" /etc/duora-telemetry/receiver.json
    install -m 0644 "$ROOT/infra/openclaw/remote/gateway/duora-telemetry-receiver.service" /etc/systemd/system/
    install -m 0644 "$ROOT/infra/openclaw/remote/gateway/observer.conf" /etc/systemd/system/duora-observer.service.d/remote.conf
    install -m 0644 "$ROOT/infra/openclaw/remote/gateway/monitor.conf" /etc/systemd/system/duora-monitor.service.d/remote.conf
    ;;
  *) echo 'Unknown profile.' >&2; exit 1 ;;
esac
install -d -m 0700 /etc/duora-telemetry/secrets
systemctl daemon-reload
printf '%s\n' 'Files installed. Nothing enabled. Provision private certificates/configuration and complete the remote runbook.'
