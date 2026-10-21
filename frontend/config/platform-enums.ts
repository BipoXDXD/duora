import type { Plugin } from 'vite'

const platformTypes = '/node_modules/@aws-amplify/core/dist/esm/Platform/types.mjs'

// Reconhece apenas o formato emitido pelo compilador para tabelas de strings locais.
// Chamadas, getters, valores calculados e inicializações com outros efeitos ficam intactos.
const stringEnum =
  /var ([A-Za-z_$][\w$]*);\r?\n\(function \(\1\) \{\r?\n((?:[ \t]*(?:\/\/[^\r\n]*|\1\["[^"\\]+"\] = "(?:[^"\\]|\\.)*";)\r?\n)+)\}\)\(\1 \|\| \(\1 = \{\}\)\);/g

export function platformEnums(): Plugin {
  return {
    name: 'platform-enums',
    enforce: 'pre',
    transform(code, id) {
      if (!id.replaceAll('\\', '/').endsWith(platformTypes)) return null
      const compacted = code.replace(stringEnum, (original: string, name: string, body: string) => {
        // O setter herdado tem semântica diferente de uma propriedade literal.
        if (body.includes('["__proto__"]')) return original
        const members = body.replace(/[A-Za-z_$][\w$]*\[("[^"\\]+")\] = ("(?:[^"\\]|\\.)*");/g, '[$1]: $2,')
        return `var ${name} = {\n${members}};`
      })
      return compacted === code ? null : { code: compacted, map: null }
    },
  }
}
