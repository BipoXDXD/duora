import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join } from 'node:path'
import { parse, type Node } from 'postcss'
import type { Plugin } from 'vite'

// Includes every state/size/variation of the primitives used by the detector,
// plus the field and icon primitives used internally by its camera selector.
export const livenessStyleModules = [
  'base',
  'alert',
  'button',
  'flex',
  'icon',
  'loader',
  'text',
  'visuallyHidden',
  '_fieldControl',
  'field',
  'fieldGroup',
  'fieldMessages',
  'fieldModifiers',
  'select',
  'selectField',
  'liveness',
] as const

const componentPrefix = '--amplify-components-'
const references = (value: string): string[] =>
  Array.from(value.matchAll(/var\(\s*(--[\w-]+)/g), (match) => match[1] ?? '')

function inKeyframes(node: Node): boolean {
  for (let parent: Node | undefined = node.parent; parent; parent = parent.parent) {
    if (
      parent.type === 'atrule' &&
      'name' in parent &&
      typeof parent.name === 'string' &&
      parent.name.endsWith('keyframes')
    )
      return true
  }
  return false
}

export function selectLivenessStyles(source: string, modules: string[]): string {
  const selectors = new Set<string>()
  for (const css of modules) {
    parse(css).walkRules((rule) => {
      selectors.add(rule.selector)
    })
  }
  const root = parse(source)
  // Filter the original stylesheet, retaining its cascade order and at-rule context.
  // Never infer state classes from rendered markup: complete upstream modules define them.
  root.walkRules((rule) => {
    if (!inKeyframes(rule) && !selectors.has(rule.selector)) rule.remove()
  })
  const dependencies = new Map<string, Set<string>>()
  const used = new Set<string>()
  root.walkDecls((declaration) => {
    const refs = references(declaration.value)
    if (declaration.prop.startsWith(componentPrefix)) {
      const values = dependencies.get(declaration.prop) ?? new Set<string>()
      refs.forEach((ref) => values.add(ref))
      dependencies.set(declaration.prop, values)
    } else {
      refs.forEach((ref) => used.add(ref))
    }
  })
  // Iterate the growing set to retain dependencies across aliases and theme overrides.
  for (const name of used) dependencies.get(name)?.forEach((ref) => used.add(ref))
  root.walkDecls((declaration) => {
    if (declaration.prop.startsWith(componentPrefix) && !used.has(declaration.prop)) declaration.remove()
  })
  root.walkAtRules((rule) => {
    if (rule.nodes?.length === 0) rule.remove()
  })
  return root.toString()
}

export function livenessStyles(): Plugin {
  return {
    name: 'duora-liveness-styles',
    enforce: 'pre',
    transform(source, id) {
      const file = id.split('?')[0] ?? id
      if (!file.replaceAll('\\', '/').endsWith('/@aws-amplify/ui-react-liveness/dist/styles.css')) return null
      const require = createRequire(file)
      const uiStyles = dirname(require.resolve('@aws-amplify/ui-react/styles/base.css'))
      const modules = livenessStyleModules.map((name) => {
        const moduleFile = join(uiStyles, `${name}.css`)
        this.addWatchFile(moduleFile)
        return readFileSync(moduleFile, 'utf8')
      })
      return { code: selectLivenessStyles(source, modules), map: null }
    },
  }
}
