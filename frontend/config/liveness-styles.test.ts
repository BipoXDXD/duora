// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { selectLivenessStyles } from './liveness-styles.ts'

const selected = `:root { --amplify-components-button-color: var(--amplify-components-field-color); }
.amplify-button:hover, .amplify-button:focus-visible { color: var(--amplify-components-button-color); }
@media (prefers-reduced-motion: reduce) { .amplify-button { animation: none; } }`
const source = `${selected}
:root {
 --amplify-components-field-color: var(--amplify-colors-font-primary);
 --amplify-components-unused-color: red;
 --amplify-colors-font-primary: black;
 --amplify-fonts-default: sans-serif;
}
.unused-widget { color: var(--amplify-components-unused-color); }
@font-face { font-family: Example; src: url(example.woff2); }
@keyframes pulse { from { opacity: 0; } to { opacity: 1; } }`

describe('liveness stylesheet selection', () => {
  it('removes unrelated components and preserves shared tokens, transitive tokens and effects', () => {
    const result = selectLivenessStyles(source, [selected, ':root {}'])
    expect(result).not.toContain('.unused-widget')
    expect(result).not.toContain('--amplify-components-unused-color')
    expect(result).toContain('--amplify-components-field-color: var(--amplify-colors-font-primary)')
    expect(result).toContain('--amplify-fonts-default: sans-serif')
    expect(result).toContain('.amplify-button:hover, .amplify-button:focus-visible')
    expect(result).toContain('@media (prefers-reduced-motion: reduce)')
    expect(result).toContain('@font-face { font-family: Example; src: url(example.woff2); }')
    expect(result).toContain('@keyframes pulse { from { opacity: 0; } to { opacity: 1; } }')
  })
})

import { readFileSync, readdirSync } from 'node:fs'
import { createRequire } from 'node:module'
import { basename, dirname, join } from 'node:path'
import { parse, type Node } from 'postcss'
import { build } from 'vite'
import { livenessStyles, livenessStyleModules } from './liveness-styles.ts'

const requireSdk = createRequire(import.meta.resolve('@aws-amplify/ui-react-liveness'))
const vendorFile = requireSdk.resolve('@aws-amplify/ui-react-liveness/styles.css')
const vendorSource = readFileSync(vendorFile, 'utf8')
const stylesDirectory = dirname(requireSdk.resolve('@aws-amplify/ui-react/styles/base.css'))
const modules = livenessStyleModules.map((name) => readFileSync(join(stylesDirectory, `${name}.css`), 'utf8'))
const filtered = selectLivenessStyles(vendorSource, modules)

function ruleRecords(css: string): string[] {
  const records: string[] = []
  parse(css).walkRules((rule) => {
    let context = ''
    for (let parent: Node | undefined = rule.parent; parent; parent = parent.parent) {
      if (parent.type === 'atrule' && 'name' in parent && 'params' in parent)
        context = `@${String(parent.name)} ${String(parent.params)}/${context}`
    }
    for (const node of rule.nodes) {
      if (node.type === 'decl')
        records.push(`${context}${rule.selector}/${node.prop}:${node.value}:${String(node.important)}`)
    }
  })
  return records
}

describe('installed detector stylesheet', () => {
  it('preserves every declaration and responsive/accessibility state of selected primitive modules', () => {
    const records = new Set(ruleRecords(filtered))
    for (const name of livenessStyleModules) {
      for (const record of ruleRecords(readFileSync(join(stylesDirectory, `${name}.css`), 'utf8'))) {
        if (name !== 'base' || !record.includes('/--amplify-components-')) {
          expect(records.has(record), `${name}: ${record}`).toBe(true)
        }
      }
    }
    const original = ruleRecords(vendorSource)
    const result = ruleRecords(filtered)
    expect(result).toEqual(original.filter((record) => records.has(record)))
  })

  it('retains original shared tokens and every referenced component token in every theme', () => {
    const original = parse(readFileSync(join(stylesDirectory, 'base.css'), 'utf8'))
    const output = parse(filtered)
    const declarations = new Map<string, string[]>()
    output.walkDecls((decl) => {
      declarations.set(decl.prop, [...(declarations.get(decl.prop) ?? []), decl.value])
    })
    original.walkDecls((decl) => {
      if (decl.prop.startsWith('--amplify-') && !decl.prop.startsWith('--amplify-components-')) {
        expect(declarations.get(decl.prop)).toContain(decl.value)
      }
    })
    output.walkDecls((decl) => {
      for (const [, name] of decl.value.matchAll(/var\(\s*(--amplify-components-[\w-]+)/g)) {
        if (!name) throw new Error('Missing variable reference')
        expect(declarations.has(name), `${decl.prop} -> ${name}`).toBe(true)
      }
    })
    expect(filtered.length).toBeLessThan(vendorSource.length / 2)
  })

  it('keeps styles for all component classes reached by detector primitive imports', () => {
    const { ComponentClassName: componentNames } = requireSdk('@aws-amplify/ui') as {
      ComponentClassName: Record<string, string>
    }
    const detectorDirectory = join(dirname(vendorFile), 'esm')
    const primitiveDirectory = join(dirname(requireSdk.resolve('@aws-amplify/ui-react')), 'esm', 'primitives')
    const primitiveFiles = readdirSync(primitiveDirectory, { recursive: true, encoding: 'utf8' })
    const selectorClasses = (css: string) => {
      const names = new Set<string>()
      parse(css).walkRules((rule) => {
        for (const [, name] of rule.selector.matchAll(/\.([\w-]+)/g)) {
          if (name) names.add(name)
        }
      })
      return names
    }
    const originalClasses = selectorClasses(vendorSource)
    const retainedClasses = selectorClasses(filtered)
    const visited = new Set<string>()
    const pending: string[] = []
    for (const file of readdirSync(detectorDirectory, { recursive: true, encoding: 'utf8' }).filter((entry) =>
      entry.endsWith('.mjs'),
    )) {
      const detectorSource = readFileSync(join(detectorDirectory, file), 'utf8')
      for (const [, names] of detectorSource.matchAll(
        /import \{ ([^}]+) \} from '@aws-amplify\/ui-react(?:\/internal)?';/g,
      )) {
        for (const name of (names ?? '').split(', ')) {
          if (name.startsWith('use')) continue
          const primitiveFile = primitiveFiles.find((fileName) => basename(fileName) === `${name}.mjs`)
          if (!primitiveFile) throw new Error(`Unreviewed detector primitive: ${name}`)
          pending.push(join(primitiveDirectory, primitiveFile))
        }
      }
    }
    while (pending.length) {
      const file = pending.pop()
      if (!file) throw new Error('Missing module path')
      if (visited.has(file)) continue
      visited.add(file)
      const primitiveSource = readFileSync(file, 'utf8')
      for (const [, name] of primitiveSource.matchAll(/ComponentClassName\.(\w+)/g)) {
        const className = componentNames[name ?? '']
        if (!className) throw new Error(`Unknown component ${name ?? ''}`)
        if (originalClasses.has(className)) expect(retainedClasses.has(className), `${file}: ${className}`).toBe(true)
      }
      for (const [, relative] of primitiveSource.matchAll(/(?:from |import )'([^']+\.mjs)'/g)) {
        if (relative?.startsWith('.')) pending.push(join(dirname(file), relative))
      }
    }
    expect(visited.size).toBeGreaterThan(10)
  })

  it('applies selection through Vite without modifying vendor files', async () => {
    const result = await build({
      configFile: false,
      logLevel: 'silent',
      plugins: [
        livenessStyles(),
        {
          name: 'css-fixture',
          resolveId: (id) => (id === '/virtual/liveness-style-fixture.js' ? '\0fixture' : null),
          load: (id) => (id === '\0fixture' ? `import ${JSON.stringify(vendorFile)}` : null),
        },
      ],
      build: {
        write: false,
        minify: false,
        cssMinify: false,
        lib: { entry: '/virtual/liveness-style-fixture.js', formats: ['es'] },
      },
    })
    const bundle = Array.isArray(result) ? result[0] : result
    if (!bundle || !('output' in bundle)) throw new Error('Expected bundle')
    const css = bundle.output.find((asset) => asset.type === 'asset' && asset.fileName.endsWith('.css'))
    if (css?.type !== 'asset') throw new Error('Expected stylesheet')
    expect(ruleRecords(String(css.source))).toEqual(ruleRecords(filtered))
    expect(readFileSync(vendorFile, 'utf8')).toBe(vendorSource)
  })
})
