# OpenClaw Remote VPS Implementation Plan

> Execution: superpowers:executing-plans, inline in this isolated branch. The user requested implementation and a PR after the topology comparison; no additional deployment approval is implied.

**Goal:** Preserve the existing Slack/MCP integration while moving its gateway to an independent VPS.
**Architecture:** Duora sends a fixed sanitized snapshot over TLS with mandatory client certificate and server hostname/CA verification. A distinct unprivileged receiver validates the client fingerprint, exact schema, freshness and a durable timestamp watermark before publication. Monitor combines remote product telemetry and local gateway health; MCP remains local read-only stdio.
**Tech Stack:** Python 3.12 standard library (ssl/urllib/http.server), existing Node MCP, Docker Compose, systemd. No new dependencies.
**Spec:** INV-12/T-12, T-08, ADR-0013/0014; docs/operations/openclaw.md. No product API changes.

## Global Constraints
No production secrets, SSH, purchases, activation, merge, workflow/policy edits or product data. Maintain existing colocated profile. Source host has no inbound listener. Remote gateway receives no Docker/SSH/DB/transport private key. Source and gateway installs are fresh-only and inactive. No builds on VPS. Failure/staleness must never mean healthy.

## Review Focus
1. Certificate rejection, invalid hostname, untrusted CA, expired certificate and unpinned identity.
2. Replay across receiver restart, equal timestamp retry after lost ACK, stale/future/NaN/bool timestamps.
3. Unknown/missing keys, nested data, duplicate JSON keys, oversize/chunked bodies and redirect.
4. Sender/receiver timeouts, unavailable transport and stale telemetry in monitor/MCP.
5. Separate resource budgets and prohibition on running remote gateway beside Duora containers.

### Task 1: Protocol and transport
Files: scripts/openclaw/telemetry.py, telemetry_sender.py, telemetry_receiver.py, test_telemetry.py.
- [x] Write tests: exact check keys/booleans, duplicate keys, invalid timestamps, packet size; durable replay and HTTP framing.
- [x] Run discover -s scripts/openclaw -p test_telemetry.py and observe missing implementation.
- [x] Implement sanitizer snapshot(payload, now), Store.accept(body, peer_fingerprint, now), TLS contexts, bounded receiver, sender with no redirect or proxy.
- [x] Add real loopback mTLS test with ephemeral openssl certificates; verify success, replay, wrong client, missing client, wrong server identity and CA; never save private keys in repo.

### Task 2: Profiles and integration
Files: collect.py, monitor.py, capacity.py, install.sh, install-remote.sh, infra/openclaw/remote/*, test_remote_profile.py, scripts/deploy/test_openclaw_capacity.py.
- [x] Write tests for source collection without gateway, remote health merge preserving original timestamp, capacity source8512/gateway3456, no prod container coexistence, unit boundaries and installer no starts.
- [x] Implement collectors source/gateway modes; monitor reads validated remote snapshot plus local gateway snapshot, treating invalid/stale input as failure.
- [x] Add explicit root-owned remote-gateway profile, separate gate, TLS LoadCredential units, bounded receiver128MiB/sender64MiB, deny privileged mounts/network exposure to gateway.
- [x] Add inactive fresh-only source and gateway installs, reuse existing image activation/firewall/rollback.
- [x] Run all OpenClaw and cross-stack regressions, Compose normalization, shell syntax, pyright, Ansible validation and scanners.

### Task 3: Runbook and delivery
Files: docs/operations/openclaw.md, capacity.md, openclaw-remote.md; ADR for the cross-host boundary.
- [x] Document exact installation order, isolated PKI, rotation, firewall source allowlist, certificate expiry, stale data, replay state, recovery, separate budgets, tests/limits and pending two-host/mobile acceptance.
- [x] Request independent whole-branch review; fix actionable findings and repeat affected tests.
- [x] Commit/push feature branch and open draft PR; attach it. No merge or deployment.


## Execution record
2026-10-21: executed inline on branch codex/openclaw-remote-vps from main 26bf417, synthetic data only.
Contract tests failed before implementation; TLS fixtures use temporary PKI. Follow-up regressions caught outage recovery and post-rename disk failure before ACK. Independent review found and verified fixes for absent InaccessiblePaths and ACK durability; final independent protocol suite 13/13 passed, no remaining actionable findings.
Final local ops: 365 discovered, 364 passed, 1 opt-in browser probe skipped; children include 31 OpenClaw Python and 7 Node/MCP tests. Types, Ansible, Compose, shell and scanners passed. Full CI runs after PR creation; actual Linux enforcement, two-host transport/load/restore/mobile and production authorization remain pending. PR delivery completes Task3; no merge/deploy/activation.
