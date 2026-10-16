import { screen, waitFor, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { createTestQueryClient, renderApp } from '@/test/render'

const ME = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@exemplo.com',
  status: 'Active',
  emailConfirmed: true,
}
const CURRENT = {
  id: 'sessao-atual',
  createdAt: '2026-10-14T10:00:00Z',
  lastSeenAt: '2026-10-15T09:30:00Z',
  current: true,
  description: 'Safari no macOS',
}
const CHUNKED_NAVIGATION_TIMEOUT_MS = 5000

const OTHER = { ...CURRENT, id: 'sessao-outra', current: false, description: 'Firefox no Ubuntu' }

function useMeEndpoint(respond: () => Response = () => HttpResponse.json(ME)) {
  server.use(http.get(`${API}/me`, respond))
}

function useSessionsEndpoint(respond: () => Response = () => HttpResponse.json({ items: [CURRENT, OTHER] })) {
  let calls = 0
  server.use(
    http.get(`${API}/me/sessions`, () => {
      calls += 1
      return respond()
    }),
  )
  return { calls: () => calls }
}

describe('rota protegida', () => {
  it('sem sessão (401) leva à entrada pedindo para entrar, lembrando de onde veio', async () => {
    useMeEndpoint(() => problem(401, 'UNAUTHENTICATED'))
    await renderApp('/conta')

    // Dois chunks sob demanda (guarda e entrada) antes do título: a espera padrão de 1 s falhava em CI carregado.
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Entrar' }, { timeout: CHUNKED_NAVIGATION_TIMEOUT_MS }),
    ).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Entre para continuar.')
  })

  it('mostra carregando enquanto confirma a identidade', async () => {
    useMeEndpoint()
    useSessionsEndpoint()
    await renderApp('/conta')

    expect(screen.getByRole('status')).toHaveTextContent('Carregando sua conta…')
  })

  it('erro de rede na identidade não é tratado como sessão encerrada', async () => {
    useMeEndpoint(() => HttpResponse.error())
    await renderApp('/conta')

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
    expect(screen.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Tentar de novo' })).toBeInTheDocument()
  })

  it('tentar de novo recupera a área depois da falha de rede', async () => {
    let online = false
    useMeEndpoint(() => (online ? HttpResponse.json(ME) : HttpResponse.error()))
    useSessionsEndpoint()
    const { user } = await renderApp('/conta')

    await screen.findByRole('alert')
    online = true
    await user.click(screen.getByRole('button', { name: 'Tentar de novo' }))

    expect(await screen.findByText('ana@exemplo.com')).toBeInTheDocument()
  })
})

describe('área da conta', () => {
  it('mostra e-mail, situação e as sessões, marcando a corrente', async () => {
    useMeEndpoint()
    useSessionsEndpoint()
    await renderApp('/conta')

    expect(await screen.findByText('ana@exemplo.com')).toBeInTheDocument()
    expect(document.title).toBe('Sua conta · Duora')
    expect(screen.getByText('Ativa')).toBeInTheDocument()
    const items = await screen.findAllByRole('listitem')
    expect(items).toHaveLength(2)
    expect(within(items[0] ?? document.body).getByText('Esta sessão')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Encerrar esta sessão: Safari no macOS' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Encerrar sessão: Firefox no Ubuntu' })).toBeInTheDocument()
  })

  it('mostra instantes no formato do idioma, com o instante UTC preservado no atributo', async () => {
    useMeEndpoint()
    useSessionsEndpoint()
    await renderApp('/conta')

    const items = await screen.findAllByRole('listitem')
    const createdAt = within(items[0] ?? document.body).getByText('14/10/2026, 10:00')
    expect(createdAt.tagName).toBe('TIME')
    expect(createdAt).toHaveAttribute('datetime', CURRENT.createdAt)
  })

  it('em inglês, textos, situação e datas seguem o idioma ativo', async () => {
    useMeEndpoint()
    useSessionsEndpoint()
    await renderApp('/conta', createTestQueryClient(), 'en')

    expect(await screen.findByRole('heading', { level: 1, name: 'Your account' })).toBeInTheDocument()
    expect(document.title).toBe('Your account · Duora')
    expect(screen.getByText('Active')).toBeInTheDocument()
    const items = await screen.findAllByRole('listitem')
    expect(within(items[0] ?? document.body).getByText('This session')).toBeInTheDocument()
    expect(within(items[0] ?? document.body).getByText('10/14/26, 10:00 AM')).toBeInTheDocument()
    expect(within(items[0] ?? document.body).getByText('10/15/26, 9:30 AM')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'End this session: Safari no macOS' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'End session: Firefox no Ubuntu' })).toBeInTheDocument()
  })

  it('e-mail pendente avisa que falta confirmar', async () => {
    useMeEndpoint(() => HttpResponse.json({ ...ME, status: 'PendingEmail', emailConfirmed: false }))
    useSessionsEndpoint()
    await renderApp('/conta')

    expect(await screen.findByText('Aguardando confirmação do e-mail')).toBeInTheDocument()
    expect(screen.getAllByRole('status')[0]).toHaveTextContent('Confirme seu e-mail para continuar.')
  })

  it('situação desconhecida não quebra a página', async () => {
    useMeEndpoint(() => HttpResponse.json({ ...ME, status: 'EstadoNovo' }))
    useSessionsEndpoint()
    await renderApp('/conta')

    expect(await screen.findByText('Indisponível no momento')).toBeInTheDocument()
  })

  it('lista vazia é diferente de erro', async () => {
    useMeEndpoint()
    useSessionsEndpoint(() => HttpResponse.json({ items: [] }))
    await renderApp('/conta')

    expect(await screen.findByText('Nenhuma sessão ativa encontrada.')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('erro de rede nas sessões não é mostrado como lista vazia', async () => {
    useMeEndpoint()
    useSessionsEndpoint(() => HttpResponse.error())
    await renderApp('/conta')

    expect(await screen.findByText('Não foi possível carregar as sessões.')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Confira sua conexão')
    expect(screen.queryByText('Nenhuma sessão ativa encontrada.')).not.toBeInTheDocument()
  })

  it('instante fora do contrato (+00:00) cai no estado de erro, sem derrubar a página', async () => {
    useMeEndpoint()
    useSessionsEndpoint(() => HttpResponse.json({ items: [{ ...CURRENT, createdAt: '2026-10-14T10:00:00+00:00' }] }))
    await renderApp('/conta')

    expect(await screen.findByText('Não foi possível carregar as sessões.')).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Algo deu errado do nosso lado.')
    expect(screen.getByText('ana@exemplo.com')).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Esta parte do Duora parou de funcionar' })).not.toBeInTheDocument()
  })

  it('sessão que expira durante o uso leva à entrada com o aviso de sessão expirada', async () => {
    let sessionAlive = true
    useMeEndpoint(() => (sessionAlive ? HttpResponse.json(ME) : problem(401, 'UNAUTHENTICATED')))
    useSessionsEndpoint(() => {
      sessionAlive = false
      return problem(401, 'UNAUTHENTICATED')
    })
    const { queryClient } = await renderApp('/conta')

    expect(await screen.findByRole('heading', { level: 1, name: 'Entrar' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Sua sessão expirou.')
    expect(screen.queryByText('ana@exemplo.com')).not.toBeInTheDocument()
    expect(queryClient.getQueryCache().getAll()).toEqual([])
  })

  it('sair limpa o cache, renova o CSRF e leva à entrada', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    useMeEndpoint()
    useSessionsEndpoint()
    let logoutToken: string | null = null
    server.use(
      http.post(`${API}/auth/logout`, ({ request }) => {
        logoutToken = request.headers.get('X-XSRF-TOKEN')
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { user, queryClient } = await renderApp('/conta')

    await user.click(await screen.findByRole('button', { name: 'Sair' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Entrar' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Você saiu da sua conta.')
    expect(logoutToken).toBe('token-1')
    await waitFor(() => {
      expect(antiforgery.calls()).toBe(2)
    })
    expect(queryClient.getQueryCache().getAll()).toEqual([])
  })

  it('falha de rede ao sair mantém a pessoa na conta com erro próprio', async () => {
    useAntiforgeryEndpoint()
    useMeEndpoint()
    useSessionsEndpoint()
    server.use(http.post(`${API}/auth/logout`, () => HttpResponse.error()))
    const { user } = await renderApp('/conta')

    await user.click(await screen.findByRole('button', { name: 'Sair' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
    expect(screen.getByText('ana@exemplo.com')).toBeInTheDocument()
  })

  it('encerrar outra sessão manda o id com CSRF e recarrega a lista', async () => {
    const antiforgery = useAntiforgeryEndpoint()
    useMeEndpoint()
    const sessions = useSessionsEndpoint()
    let revoked: { id: string; token: string | null } | null = null
    server.use(
      http.delete(`${API}/me/sessions/:id`, ({ params, request }) => {
        revoked = { id: String(params.id), token: request.headers.get('X-XSRF-TOKEN') }
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { user } = await renderApp('/conta')

    await user.click(await screen.findByRole('button', { name: 'Encerrar sessão: Firefox no Ubuntu' }))

    await waitFor(() => {
      expect(sessions.calls()).toBe(2)
    })
    expect(revoked).toEqual({ id: 'sessao-outra', token: antiforgery.latestToken() })
    expect(screen.getByText('ana@exemplo.com')).toBeInTheDocument()
  })

  it('encerrar a sessão corrente equivale a sair', async () => {
    useAntiforgeryEndpoint()
    useMeEndpoint()
    useSessionsEndpoint()
    server.use(http.delete(`${API}/me/sessions/:id`, () => new HttpResponse(null, { status: 204 })))
    const { user, queryClient } = await renderApp('/conta')

    await user.click(await screen.findByRole('button', { name: 'Encerrar esta sessão: Safari no macOS' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Entrar' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Você saiu da sua conta.')
    expect(queryClient.getQueryCache().getAll()).toEqual([])
  })

  it('401 REAUTHENTICATION_REQUIRED numa escrita, com a sessão ainda válida, não tira a pessoa da conta', async () => {
    useAntiforgeryEndpoint()
    let meCalls = 0
    useMeEndpoint(() => {
      meCalls += 1
      return HttpResponse.json(ME)
    })
    useSessionsEndpoint()
    server.use(http.delete(`${API}/me/sessions/:id`, () => problem(401, 'REAUTHENTICATION_REQUIRED')))
    const { user } = await renderApp('/conta')
    await screen.findByText('Firefox no Ubuntu')

    await user.click(screen.getByRole('button', { name: /^Encerrar sessão: Firefox no Ubuntu/ }))

    // A identidade é reconsultada (o 401 poderia ser fim de sessão) e o servidor confirma que ela segue válida.
    await waitFor(() => {
      expect(meCalls).toBe(2)
    })
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.getByRole('heading', { level: 1, name: 'Sua conta' })).toBeInTheDocument()
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/conta$/)
  })

  it('falha ao encerrar sessão mostra erro e mantém a lista', async () => {
    useAntiforgeryEndpoint()
    useMeEndpoint()
    useSessionsEndpoint()
    server.use(http.delete(`${API}/me/sessions/:id`, () => problem(500, 'INTERNAL')))
    const { user } = await renderApp('/conta')

    await user.click(await screen.findByRole('button', { name: 'Encerrar sessão: Firefox no Ubuntu' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Algo deu errado do nosso lado.')
    expect(screen.getAllByRole('listitem')).toHaveLength(2)
  })
})
