const assert = require('node:assert/strict')
const { readdirSync, realpathSync } = require('node:fs')
const { createRequire } = require('node:module')
const path = require('node:path')

const packageDirectory = realpathSync(process.argv[2])
const packageEntry = path.join(packageDirectory, 'index.js')
const virtualStore = path.resolve(packageDirectory, '../../..')
assert.equal(path.basename(virtualStore), '.pnpm')

const installed = readdirSync(virtualStore)
for (const consumer of ['vite', 'vitest', 'tinyglobby', 'fdir']) {
  const installations = installed.filter((entry) => entry.startsWith(`${consumer}@`))
  assert.ok(installations.length > 0, `No installed copy of ${consumer}`)
  for (const installation of installations) {
    const consumerManifest = path.join(virtualStore, installation, 'node_modules', consumer, 'package.json')
    const resolveForConsumer = createRequire(realpathSync(consumerManifest)).resolve
    assert.equal(realpathSync(resolveForConsumer('picomatch')), packageEntry, installation)
  }
}

const picomatch = require(packageEntry)
const globOptions = { fastpaths: false }
const image = picomatch.makeRe('+(*.webp)', globOptions)
assert.equal(image.test('profile.webp'), true)
assert.equal(image.test('profile.png'), false)
assert.equal(picomatch.parse('+(*.webp)', globOptions).output.includes(')+'), false)

const nested = picomatch.parse('+(a+(*(a)|*(b)))', globOptions)
assert.equal(nested.output.includes('[ab]*)+'), false)

const multiline = '+(draft*\nfinal)'
const parsedMultiline = picomatch.parse(multiline, { ...globOptions, bash: true, flags: 'm' })
assert.ok(parsedMultiline.tokens.some((token) => token.type === 'text' && token.value === multiline))

const include = ['src/**/*.test.{ts,tsx}', 'config/**/*.test.ts']
assert.equal(picomatch.isMatch('src/profile/edit.test.tsx', include, { dot: true }), true)
assert.equal(picomatch.isMatch('src/profile/edit.tsx', include, { dot: true }), false)
assert.equal(picomatch(include, { dot: true, ignore: ['config/**'] })('config/flags.test.ts'), false)
assert.equal(picomatch('**/.env.*', { dot: true, nocase: true })('example/.ENV.local'), true)
assert.deepEqual(picomatch.scan('src/**/button.{ts,tsx}', { parts: true }).parts, ['src', '**', 'button.{ts,tsx}'])

const captures = picomatch.makeRe('src/+(js|ts)/*.test.ts', { capture: true }).exec('src/jsts/button.test.ts')
assert.deepEqual(Array.from(captures).slice(1), ['jsts', 'button'])
