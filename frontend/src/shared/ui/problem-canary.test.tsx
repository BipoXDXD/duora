import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ApiProblemError } from '@/shared/api/api-errors'
import { parseProblemDetails } from '@/shared/api/problem-details'
import { classifyRequestFailure } from '@/shared/api/request-failure'
import { renderWithI18n } from '@/test/render'
import { RequestFailureAlert } from './alert'

const CANARY = '<img src=x onerror="window.CANARY=1">CANARY SQL password unexpected text'

for (const locale of ['pt-BR', 'en-US'] as const) {
  describe(locale, () => {
    it.each([
      'UNKNOWN',
      'constructor',
      '__proto__',
      'toString',
      CANARY,
      'VALIDATION_FAILED',
      'TEMPORARILY_UNAVAILABLE',
    ])('uses local fallback for %s', (code) => {
      const parsed = parseProblemDetails({ title: CANARY, detail: CANARY, code, errors: { email: [CANARY] } }, 400)
      if (parsed === null) throw new Error('Expected Problem Details')
      renderWithI18n(<RequestFailureAlert failure={classifyRequestFailure(new ApiProblemError(parsed))} />, locale)
      expect(screen.getByRole('alert')).toHaveTextContent(
        locale === 'pt-BR' ? 'Algo deu errado do nosso lado.' : 'Something went wrong on our side.',
      )
      expect(document.body.innerHTML).not.toContain('CANARY')
    })

    it('translates an explicit authentication code, ignoring all server text', () => {
      const parsed = parseProblemDetails({ title: CANARY, detail: CANARY, code: 'INVALID_CREDENTIALS' }, 401)
      if (parsed === null) throw new Error('Expected Problem Details')
      renderWithI18n(
        <RequestFailureAlert
          failure={classifyRequestFailure(new ApiProblemError(parsed))}
          problemMessages={{ INVALID_CREDENTIALS: 'login.invalidCredentials' }}
        />,
        locale,
      )
      expect(screen.getByRole('alert')).toHaveTextContent(
        locale === 'pt-BR' ? 'E-mail ou senha incorretos.' : 'Incorrect email or password.',
      )
      expect(document.body.innerHTML).not.toContain('CANARY')
    })
  })
}
