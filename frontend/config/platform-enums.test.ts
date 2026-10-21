// @vitest-environment node
import { runInNewContext } from 'node:vm'
import { build } from 'vite'
import { describe, expect, it } from 'vitest'
import { platformEnums } from './platform-enums.ts'

const moduleId = '/node_modules/@aws-amplify/core/dist/esm/Platform/types.mjs'
const source = `// Copyright fixture
var Framework;
(function (Framework) {
    // browser framework
    Framework["WebUnknown"] = "0";
    Framework["React"] = "1";
})(Framework || (Framework = {}));
var Category;
(function (Category) {
    Category["Auth"] = "auth";
    Category["Unused"] = "unused-category-marker";
})(Category || (Category = {}));
export { Framework, Category };`

async function bundle(options: { source?: string; id?: string; entry?: string } = {}): Promise<string> {
  const id = options.id ?? moduleId
  const entryId = '/virtual/platform-entry.js'
  const result = await build({
    configFile: false,
    logLevel: 'silent',
    plugins: [
      platformEnums(),
      {
        name: 'platform-fixture',
        resolveId: (name) => (name === entryId ? '\0entry' : name === id ? id : null),
        load: (name) =>
          name === id
            ? (options.source ?? source)
            : name === '\0entry'
              ? (options.entry ?? `export { Framework } from ${JSON.stringify(id)}`)
              : null,
      },
    ],
    build: { write: false, minify: false, lib: { entry: entryId, name: 'PlatformFixture', formats: ['iife'] } },
  })
  const output = Array.isArray(result) ? result[0] : result
  if (!output || !('output' in output)) throw new Error('Expected bundle output')
  const chunk = output.output.find((item) => item.type === 'chunk')
  if (!chunk) throw new Error('Expected entry chunk')
  return chunk.code
}

function exportsOf(code: string): unknown {
  const serialized: unknown = runInNewContext(`${code}; JSON.stringify(PlatformFixture)`)
  if (typeof serialized !== 'string') throw new Error('Expected serialized exports')
  return JSON.parse(serialized) as unknown
}

describe('platform enum bundling', () => {
  it('discards unused tables and preserves every member of the used table', async () => {
    const code = await bundle()
    expect(exportsOf(code)).toEqual({ Framework: { WebUnknown: '0', React: '1' } })
    expect(code).not.toContain('unused-category-marker')
  })

  it('preserves all members when a consumer uses the category table', async () => {
    const code = await bundle({ entry: `export { Category } from ${JSON.stringify(moduleId)}` })
    expect(exportsOf(code)).toEqual({ Category: { Auth: 'auth', Unused: 'unused-category-marker' } })
  })

  it('does not transform modules outside the platform types path', async () => {
    expect(await bundle({ id: '/vendor/types.mjs' })).toContain('unused-category-marker')
  })

  it('preserves observable initialization when a table contains more than literal assignments', async () => {
    const changed = source.replace(
      'Category["Auth"] = "auth";',
      'globalThis.platformInitialized = true;\n    Category["Auth"] = "auth";',
    )
    const code = await bundle({ source: changed })
    expect(runInNewContext(`${code}; globalThis.platformInitialized`)).toBe(true)
    expect(code).toContain('unused-category-marker')
  })
})
