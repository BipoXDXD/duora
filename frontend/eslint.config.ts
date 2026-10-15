import js from '@eslint/js'
import prettier from 'eslint-config-prettier'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import { defineConfig, globalIgnores } from 'eslint/config'
import globals from 'globals'
import tseslint from 'typescript-eslint'
import { noLiteralUiText } from './config/eslint/no-literal-ui-text.ts'

export default defineConfig([
  // schema.d.ts é gerado do contrato (pnpm api:generate, docs/api/README.md); o CI confere que não foi editado.
  globalIgnores(['dist', 'coverage', 'playwright-report', 'test-results', 'src/shared/api/schema.d.ts']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      reactHooks.configs.flat['recommended-latest'],
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    linterOptions: {
      reportUnusedDisableDirectives: 'error',
    },
    rules: {
      '@typescript-eslint/consistent-type-definitions': ['error', 'interface'],
      '@typescript-eslint/consistent-type-imports': ['error', { fixStyle: 'inline-type-imports' }],
      '@typescript-eslint/consistent-type-assertions': [
        'error',
        { assertionStyle: 'as', objectLiteralTypeAssertions: 'never' },
      ],
      '@typescript-eslint/explicit-module-boundary-types': 'error',
      '@typescript-eslint/switch-exhaustiveness-check': 'error',
      '@typescript-eslint/no-shadow': 'error',
      '@typescript-eslint/ban-ts-comment': ['error', { 'ts-expect-error': 'allow-with-description' }],
      '@typescript-eslint/max-params': ['error', { max: 3 }],
      // ETS item 72 e erasableSyntaxOnly: sem enum nem namespace; união de literais no lugar.
      'no-restricted-syntax': [
        'error',
        { selector: 'TSEnumDeclaration', message: 'Use união de literais ou objeto as const.' },
      ],
    },
  },
  {
    // Todo texto visível vem do catálogo de idiomas (src/shared/i18n); testes descrevem o que veem e ficam de fora.
    files: ['src/**/*.tsx'],
    ignores: ['src/test/**', 'src/**/*.test.tsx'],
    plugins: { duora: { rules: { 'no-literal-ui-text': noLiteralUiText } } },
    rules: { 'duora/no-literal-ui-text': 'error' },
  },
  {
    // Utilitários e arquivos de teste nunca passam por fast refresh: podem exportar funções e componentes juntos.
    files: ['src/test/**/*.{ts,tsx}', 'src/**/*.test.{ts,tsx}'],
    rules: { 'react-refresh/only-export-components': 'off' },
  },
  {
    files: ['*.config.ts', 'config/**/*.ts', 'e2e/**/*.ts'],
    languageOptions: { globals: globals.node },
  },
  prettier,
])
