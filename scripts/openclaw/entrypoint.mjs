import { readFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
const env = { ...process.env };
for (const name of ['SLACK_APP_TOKEN', 'SLACK_BOT_TOKEN', 'OPENCLAW_GATEWAY_TOKEN', 'ANTHROPIC_API_KEY']) {
  env[name] = readFileSync(`/run/secrets/${name}`, 'utf8').trim();
  if (!env[name]) throw Error(`Missing credential: ${name}`);
}
const args = process.argv.slice(2);
const child = spawn('node', ['/app/dist/index.js', ...args], { env, stdio: 'inherit' });
for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => child.kill(signal));
child.on('error', () => process.exit(1));
child.on('exit', code => process.exit(code ?? 1));
