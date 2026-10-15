import type { Rule } from 'eslint'

/*
 * Texto visível escrito direto no JSX escapa do catálogo de idiomas (src/shared/i18n). A regra
 * acusa texto com letras em três lugares: filho de elemento (`<p>Entrar</p>`), string ou template
 * como filho (`<p>{'Entrar'}</p>`) e props que viram texto (`aria-label`, `title`, `label`…).
 * Pontuação e espaços entre trechos traduzidos (`{': '}`, `' · '`) passam. Strings fora do JSX
 * (ex.: num `.ts`) não são vistas: o catálogo tipado cobre esse caso pela revisão.
 */

const HAS_LETTER = /\p{L}/u

/** Props do HTML e dos componentes do design system cujo valor é lido ou exibido como texto. */
const TEXT_PROPS: ReadonlySet<string> = new Set([
  'alt',
  'aria-description',
  'aria-label',
  'aria-placeholder',
  'aria-roledescription',
  'aria-valuetext',
  'placeholder',
  'title',
  'label',
  'pendingLabel',
  'hint',
  'intro',
  'page',
  'error',
])

interface NodeLike {
  readonly type: string
}

function isNodeLike(value: unknown): value is NodeLike {
  return typeof value === 'object' && value !== null && typeof Reflect.get(value, 'type') === 'string'
}

function readProperty(node: object, key: string): unknown {
  return Reflect.get(node, key)
}

function hasLetters(text: unknown): boolean {
  return typeof text === 'string' && HAS_LETTER.test(text)
}

/** String literal ou template cujo texto fixo tem letras. */
function isLiteralText(expression: unknown): boolean {
  if (!isNodeLike(expression)) {
    return false
  }
  if (expression.type === 'Literal') {
    return hasLetters(readProperty(expression, 'value'))
  }
  if (expression.type === 'TemplateLiteral') {
    const quasis = readProperty(expression, 'quasis')
    return Array.isArray(quasis) && quasis.some((quasi: unknown) => isNodeLike(quasi) && hasTemplateLetters(quasi))
  }
  return false
}

function hasTemplateLetters(quasi: NodeLike): boolean {
  const value = readProperty(quasi, 'value')
  return typeof value === 'object' && value !== null && hasLetters(readProperty(value, 'cooked'))
}

function isElementChild(node: object): boolean {
  const parent = readProperty(node, 'parent')
  return isNodeLike(parent) && (parent.type === 'JSXElement' || parent.type === 'JSXFragment')
}

function attributeName(attribute: object): string | null {
  const name = readProperty(attribute, 'name')
  if (!isNodeLike(name) || name.type !== 'JSXIdentifier') {
    return null
  }
  const text = readProperty(name, 'name')
  return typeof text === 'string' ? text : null
}

function isLiteralAttributeValue(value: unknown): boolean {
  if (!isNodeLike(value)) {
    return false
  }
  if (value.type === 'Literal') {
    return hasLetters(readProperty(value, 'value'))
  }
  return value.type === 'JSXExpressionContainer' && isLiteralText(readProperty(value, 'expression'))
}

export const noLiteralUiText: Rule.RuleModule = {
  meta: {
    type: 'problem',
    docs: { description: 'Proíbe texto visível fora do catálogo de idiomas no JSX.' },
    schema: [],
    messages: {
      literalText: "Texto visível fora do catálogo de idiomas: crie uma chave em src/shared/i18n e use t('…').",
    },
  },
  create(context) {
    return {
      JSXText(node: Rule.Node) {
        if (hasLetters(readProperty(node, 'value'))) {
          context.report({ node, messageId: 'literalText' })
        }
      },
      JSXExpressionContainer(node: Rule.Node) {
        if (isElementChild(node) && isLiteralText(readProperty(node, 'expression'))) {
          context.report({ node, messageId: 'literalText' })
        }
      },
      JSXAttribute(node: Rule.Node) {
        const name = attributeName(node)
        if (name !== null && TEXT_PROPS.has(name) && isLiteralAttributeValue(readProperty(node, 'value'))) {
          context.report({ node, messageId: 'literalText' })
        }
      },
    }
  },
}
