// Read-only MCP transport; no exec, network, caller-supplied paths, or production credentials.
import { readFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
const schema = { type: 'object', properties: {}, additionalProperties: false };
const names = ['status', 'backups', 'actions', 'change_policy'];
function result(value, error = false) {
  return { content: [{ type: 'text', text: JSON.stringify(value) }], isError: error };
}
export function call(name, args, snapshot, now = Date.now() / 1000) {
  if (!names.includes(name) || !args || Object.keys(args).length) return result('invalid_tool_or_arguments', true);
  if (name === 'change_policy') return result({ authorized: false, executed: false,
    instruction: 'Operação privilegiada exige autorização explícita fora do agente, vinculada à ação, alvo, release/digest, janela e rollback. Use o executor restrito e o runbook existente. Um sim no Slack NÃO executa alterações. Restore, exclusão, firewall e rotação requerem operador técnico.' });
  if (!Number.isFinite(snapshot?.collected_at) || now - snapshot.collected_at > 120 || now < snapshot.collected_at)
    return result('telemetry_stale_or_unavailable', true);
  if (name === 'actions') return result({ collected_at: snapshot.collected_at, github: snapshot.github,
    checks: Object.fromEntries(Object.entries(snapshot.checks).filter(([k]) => k.startsWith('github:'))) });
  const checks = name === 'backups' ? Object.fromEntries(Object.entries(snapshot.checks)
    .filter(([k]) => /backup|media|host-fresh/.test(k))) : snapshot.checks;
  return result({ collected_at: snapshot.collected_at, checks });
}
export function rpc(message, read = () => JSON.parse(readFileSync('/telemetry/status.json', 'utf8'))) {
  if (message.id === undefined) return null;
  let value;
  if (message.method === 'initialize') value = { protocolVersion: '2024-11-05',
    capabilities: { tools: {} }, serverInfo: { name: 'duora-ops', version: '1.0.0' } };
  else if (message.method === 'ping') value = {};
  else if (message.method === 'tools/list') value = { tools: names.map(name => ({ name,
    description: `Duora ${name}: sanitized read-only telemetry or manual change policy`,
    inputSchema: schema, annotations: { readOnlyHint: true, destructiveHint: false, openWorldHint: false } })) };
  else if (message.method === 'tools/call') {
    try { value = call(message.params?.name, message.params?.arguments ?? {},
      message.params?.name === 'change_policy' ? null : read()); }
    catch { value = result('telemetry_unavailable', true); }
    process.stderr.write(JSON.stringify({ event: 'ops_query', tool: names.includes(message.params?.name)
      ? message.params.name : 'invalid', failed: value.isError }) + '\n');
  } else return { jsonrpc: '2.0', id: message.id, error: { code: -32601, message: 'Method not found' } };
  return { jsonrpc: '2.0', id: message.id, result: value };
}
if (process.argv[1]?.endsWith('ops-mcp.mjs')) {
  const lines = createInterface({ input: process.stdin });
  lines.on('line', line => {
    let response;
    try { if (line.length > 8192) throw Error(); response = rpc(JSON.parse(line)); }
    catch { response = { jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } }; }
    if (response) process.stdout.write(JSON.stringify(response) + '\n');
  });
}
