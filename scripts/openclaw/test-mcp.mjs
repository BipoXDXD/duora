import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { call, rpc } from './ops-mcp.mjs';
const snapshot = { collected_at: 100, checks: { 'backup:recent': false, 'service:web': true }, github: {} };
test('queries reject stale, future and missing telemetry', () => {
  for (const value of [null, {}, { collected_at: 500 }, { collected_at: -500 }])
    assert.equal(call('status', {}, value, 100).isError, true);
});
test('read-only catalog contains no execution or approvals', () => {
  const tools = rpc({ id: 1, method: 'tools/list' }).result.tools;
  assert.deepEqual(tools.map(t => t.name), ['status', 'backups', 'actions', 'change_policy']);
  assert.ok(tools.every(t => t.annotations.readOnlyHint));
});
test('arbitrary commands and paths are rejected', () => {
  for (const name of ['exec', 'sudo', 'deploy', '../secrets', 'approve'])
    assert.equal(call(name, {}, snapshot, 100).isError, true);
  assert.equal(call('status', { command: 'rm -rf /' }, snapshot, 100).isError, true);
});
test('change request never executes or grants authorization', () => {
  const policy = JSON.parse(call('change_policy', {}, null, 100).content[0].text);
  assert.equal(policy.authorized, false); assert.equal(policy.executed, false);
});
test('backup query excludes unrelated host data', () => {
  const value = JSON.parse(call('backups', {}, snapshot, 100).content[0].text);
  assert.deepEqual(value.checks, { 'backup:recent': false });
});
test('missing snapshot is a tool error, not healthy', () => {
  assert.equal(rpc({ id: 1, method: 'tools/call', params: { name: 'status' } }, () => { throw Error(); }).result.isError, true);
});
test('actual stdio lifecycle initializes and lists tools', () => {
  const input = [{ id: 1, method: 'initialize' }, { method: 'notifications/initialized' },
    { id: 2, method: 'tools/list' }, { id: 3, method: 'tools/call', params: { name: 'change_policy', arguments: {} } }]
    .map(x => JSON.stringify({ jsonrpc: '2.0', ...x })).join('\n') + '\n';
  const run = spawnSync(process.execPath, [new URL('./ops-mcp.mjs', import.meta.url).pathname], { input, encoding: 'utf8' });
  assert.equal(run.status, 0);
  const replies = run.stdout.trim().split('\n').map(JSON.parse);
  assert.equal(replies.length, 3); assert.equal(replies[1].result.tools.length, 4);
  assert.equal(JSON.parse(replies[2].result.content[0].text).executed, false);
});
