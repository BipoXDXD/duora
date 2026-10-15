// @vitest-environment node
import { RuleTester } from 'eslint'
import tseslint from 'typescript-eslint'
import { describe, it } from 'vitest'
import { noLiteralUiText } from './no-literal-ui-text.ts'

RuleTester.describe = describe
RuleTester.it = it
RuleTester.itOnly = it.only

const ruleTester = new RuleTester({
  languageOptions: {
    parser: tseslint.parser,
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
})

const literalText = [{ messageId: 'literalText' }]

ruleTester.run('no-literal-ui-text', noLiteralUiText, {
  valid: [
    { code: "const a = <p>{t('login.title')}</p>" },
    { code: "const a = <Page title={t('login.title')} intro={t('register.intro')} />" },
    { code: "const a = <button aria-label={t('field.showPasswordName')}>{label}</button>" },
    // Pontuação e espaços entre trechos traduzidos não são texto a traduzir.
    { code: "const a = <p>{t('a')}: <time>{d}</time>{' · '}{t('b')}{': '}</p>" },
    { code: 'const a = <p>  ·  —  (1/2)  </p>' },
    // Atributos técnicos podem ter letras.
    { code: 'const a = <div className="flex gap-2" data-testid="anuncio-de-rota" id="titulo" lang="en" />' },
    { code: 'const a = <input type="email" autoComplete="email" inputMode="email" />' },
    { code: 'const a = <a href="#conteudo-principal" />' },
  ],
  invalid: [
    { code: 'const a = <p>Página não encontrada</p>', errors: literalText },
    { code: 'const a = <p>\n  Entrar\n</p>', errors: literalText },
    { code: "const a = <p>{'Entrar'}</p>", errors: literalText },
    { code: 'const a = <p>{`Olá, ${name}`}</p>', errors: literalText },
    { code: 'const a = <button aria-label="Fechar" />', errors: literalText },
    { code: "const a = <img alt={'Foto de perfil'} />", errors: literalText },
    { code: 'const a = <Page title="Sua conta" />', errors: literalText },
    { code: 'const a = <DocumentTitle page="Entrar" />', errors: literalText },
    {
      code: 'const a = <SubmitButton label="Salvar" pendingLabel="Salvando…" />',
      errors: [...literalText, ...literalText],
    },
    { code: 'const a = <input placeholder="nome@exemplo.com" />', errors: literalText },
    { code: 'const a = <>Duora</>', errors: literalText },
  ],
})
