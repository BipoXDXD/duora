import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp, ROUTE_CHUNK_TIMEOUT_MS, type RenderedApp } from '@/test/render'

const ADMIN_WITHOUT_MFA = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'admin@example.test',
  status: 'Active',
  emailConfirmed: true,
  roles: ['operator'],
  mfaEnabled: false,
}
const SHARED_KEY = 'SYNTHETICKEY'

/** Fase do MFA como o servidor a expõe: toda rota fora do cadastro responde 403 MFA_ENROLLMENT_REQUIRED. */
function mfaPhase() {
  server.use(http.get(`${API}/me`, () => problem(403, 'MFA_ENROLLMENT_REQUIRED')))
}

function useEnrollmentEndpoint(
  respond: () => Response = () => HttpResponse.json({ sharedKey: SHARED_KEY, authenticatorUri: 'otpauth://totp/test' }),
) {
  let calls = 0
  server.use(
    http.post(`${API}/me/mfa/enrollments`, () => {
      calls++
      return respond()
    }),
  )
  return { calls: () => calls }
}

function useConfirmationEndpoint(respond: () => Response) {
  let calls = 0
  server.use(
    http.post(`${API}/me/mfa/confirmations`, () => {
      calls++
      return respond()
    }),
  )
  return { calls: () => calls }
}

async function startEnrollment(user: RenderedApp['user']) {
  await user.click(
    await screen.findByRole('button', { name: 'Configurar autenticador' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
  )
  expect(await screen.findByText(SHARED_KEY)).toBeInTheDocument()
}

async function submitCode(user: RenderedApp['user'], code: string) {
  await user.type(screen.getByLabelText('Código do autenticador'), code)
  await user.click(screen.getByRole('button', { name: 'Ativar MFA' }))
}

describe('fase do cadastro de MFA', () => {
  it.each(['/conta', '/conta/perfil', '/trocar-senha-inicial', '/conta/ativar-mfa'])(
    '403 MFA_ENROLLMENT_REQUIRED em /me leva ao cadastro: %s',
    async (path) => {
      mfaPhase()
      await renderApp(path)
      expect(
        await screen.findByRole(
          'heading',
          { name: 'Ative a autenticação em duas etapas' },
          { timeout: ROUTE_CHUNK_TIMEOUT_MS },
        ),
      ).toBeInTheDocument()
      expect(screen.getByTestId('location')).toHaveTextContent(/^\/conta\/ativar-mfa$/)
    },
  )

  it('conta privilegiada sem MFA que abre /conta/perfil vai ao cadastro', async () => {
    server.use(
      http.get(`${API}/me`, () => HttpResponse.json(ADMIN_WITHOUT_MFA)),
      http.get(`${API}/me/profile`, () => problem(403, 'MFA_ENROLLMENT_REQUIRED')),
    )
    await renderApp('/conta/perfil')
    expect(
      await screen.findByRole(
        'heading',
        { name: 'Ative a autenticação em duas etapas' },
        { timeout: ROUTE_CHUNK_TIMEOUT_MS },
      ),
    ).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/conta\/ativar-mfa$/)
  })

  it.each(['12345', '1234567', 'abcdef', '12 456'])('recusa o código %s sem chamar a API', async (code) => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    const confirmation = useConfirmationEndpoint(() => HttpResponse.json({ recoveryCodes: ['x'] }))
    const { user } = await renderApp('/conta/ativar-mfa')
    await startEnrollment(user)

    await submitCode(user, code)

    expect(screen.getByLabelText('Código do autenticador')).toHaveAccessibleDescription(
      'Informe um código válido de 6 dígitos do autenticador.',
    )
    expect(confirmation.calls()).toBe(0)
  })

  it.each([
    [400, 'MFA_CODE_INVALID', 'Informe um código válido de 6 dígitos'],
    [401, 'REAUTHENTICATION_REQUIRED', 'Entre novamente para confirmar sua identidade'],
    [403, 'CSRF_VALIDATION_FAILED', 'Não foi possível validar esta tentativa'],
    [409, 'MFA_ALREADY_ENABLED', 'O MFA já está ativo'],
    [409, 'MFA_ENROLLMENT_NOT_STARTED', 'A configuração expirou'],
  ])('confirmação com %s %s mostra a orientação, sem retry', async (status, code, message) => {
    mfaPhase()
    useAntiforgeryEndpoint()
    useEnrollmentEndpoint()
    const confirmation = useConfirmationEndpoint(() => problem(status, code))
    const { user } = await renderApp('/conta/ativar-mfa')
    await startEnrollment(user)

    await submitCode(user, '123456')

    expect(await screen.findByRole('alert')).toHaveTextContent(message)
    expect(confirmation.calls()).toBe(1)
  })

  it.each([
    [401, 'REAUTHENTICATION_REQUIRED', 'Entre novamente para confirmar sua identidade'],
    [409, 'MFA_ALREADY_ENABLED', 'O MFA já está ativo'],
  ])('início do cadastro com %s %s mostra a orientação', async (status, code, message) => {
    mfaPhase()
    useAntiforgeryEndpoint()
    const enrollment = useEnrollmentEndpoint(() => problem(status, code))
    const { user } = await renderApp('/conta/ativar-mfa')

    await user.click(
      await screen.findByRole('button', { name: 'Configurar autenticador' }, { timeout: ROUTE_CHUNK_TIMEOUT_MS }),
    )

    expect(await screen.findByRole('alert')).toHaveTextContent(message)
    expect(screen.queryByText(SHARED_KEY)).not.toBeInTheDocument()
    expect(enrollment.calls()).toBe(1)
  })
})
