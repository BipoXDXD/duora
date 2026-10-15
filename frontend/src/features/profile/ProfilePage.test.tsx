import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { profileQueryKey } from '@/features/session/query-keys'
import { API, problem, server, useAntiforgeryEndpoint } from '@/test/msw'
import { renderApp } from '@/test/render'

const ME = {
  id: '0199f1a2-3b4c-7d5e-8f60-718293a4b5c6',
  email: 'ana@exemplo.com',
  status: 'Active',
  emailConfirmed: true,
}

interface StoredProfile {
  displayName: string | null
  bio: string | null
  version: number
}

interface SentPatch {
  readonly ifMatch: string | null
  readonly body: unknown
}

/** Backend do perfil com ETag: grava só se o If-Match bater com a versão atual, como o servidor real. */
function useProfileBackend(initial: StoredProfile = { displayName: null, bio: null, version: 0 }) {
  const stored: StoredProfile = { ...initial }
  const patches: SentPatch[] = []
  const toJson = () => ({ displayName: stored.displayName, bio: stored.bio, version: String(stored.version) })
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(ME)),
    http.get(`${API}/me/sessions`, () => HttpResponse.json({ items: [] })),
    http.get(`${API}/me/profile`, () =>
      HttpResponse.json(toJson(), { headers: { ETag: `"${String(stored.version)}"` } }),
    ),
    http.patch(`${API}/me/profile`, async ({ request }) => {
      const body = (await request.json()) as Partial<StoredProfile>
      const ifMatch = request.headers.get('If-Match')
      patches.push({ ifMatch, body })
      if (ifMatch === null) {
        return problem(428, 'PRECONDITION_REQUIRED')
      }
      if (ifMatch !== `"${String(stored.version)}"`) {
        return problem(412, 'PRECONDITION_FAILED')
      }
      if (body.displayName !== undefined) {
        stored.displayName = body.displayName
      }
      if (body.bio !== undefined) {
        stored.bio = body.bio
      }
      stored.version += 1
      return HttpResponse.json(toJson(), { headers: { ETag: `"${String(stored.version)}"` } })
    }),
  )
  useAntiforgeryEndpoint()
  return {
    patches,
    /** Outra aba ou aparelho gravou por fora desta tela. */
    changeElsewhere(change: Partial<Omit<StoredProfile, 'version'>>) {
      Object.assign(stored, change)
      stored.version += 1
    },
  }
}

describe('perfil: leitura', () => {
  it('mostra carregando e depois os valores salvos nos campos', async () => {
    useProfileBackend({ displayName: 'Ana', bio: 'Gosto de jogos.', version: 3 })
    await renderApp('/conta/perfil')

    expect(await screen.findByText('Carregando seu perfil…')).toBeInTheDocument()
    expect(await screen.findByLabelText('Nome de exibição')).toHaveValue('Ana')
    expect(screen.getByLabelText('Descrição curta')).toHaveValue('Gosto de jogos.')
    expect(document.title).toBe('Seu perfil · Duora')
  })

  it('falha de carga mostra o erro e tentar de novo recupera', async () => {
    useProfileBackend({ displayName: 'Ana', bio: null, version: 1 })
    let failing = true
    server.use(
      http.get(`${API}/me/profile`, () =>
        failing
          ? problem(404, 'NOT_FOUND')
          : HttpResponse.json({ displayName: 'Ana', bio: null, version: '1' }, { headers: { ETag: '"1"' } }),
      ),
    )
    const { user } = await renderApp('/conta/perfil')

    expect(await screen.findByText('Não foi possível carregar seu perfil.')).toBeInTheDocument()
    failing = false
    await user.click(screen.getByRole('button', { name: 'Tentar de novo' }))

    expect(await screen.findByLabelText('Nome de exibição')).toHaveValue('Ana')
  })

  it('a conta tem o link para editar o perfil', async () => {
    useProfileBackend()
    const { user } = await renderApp('/conta')

    await user.click(await screen.findByRole('link', { name: 'Editar perfil' }))

    expect(await screen.findByRole('heading', { level: 1, name: 'Seu perfil' })).toBeInTheDocument()
  })
})

describe('perfil: edição', () => {
  it('salva com o ETag lido e usa a versão nova no envio seguinte', async () => {
    const backend = useProfileBackend()
    const { user } = await renderApp('/conta/perfil')

    await user.type(await screen.findByLabelText('Nome de exibição'), 'Ana')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    expect(await screen.findByText('Perfil salvo.')).toBeInTheDocument()
    await user.type(screen.getByLabelText('Descrição curta'), 'Oi')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    await waitFor(() => {
      expect(backend.patches).toHaveLength(2)
    })
    expect(backend.patches).toEqual([
      { ifMatch: '"0"', body: { displayName: 'Ana' } },
      { ifMatch: '"1"', body: { bio: 'Oi' } },
    ])
  })

  it('valida no navegador antes de enviar', async () => {
    const backend = useProfileBackend()
    const { user } = await renderApp('/conta/perfil')

    await screen.findByLabelText('Nome de exibição')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    expect(await screen.findByText('Informe um nome de exibição.')).toBeInTheDocument()
    expect(screen.getByLabelText('Nome de exibição')).toHaveAttribute('aria-invalid', 'true')
    expect(backend.patches).toHaveLength(0)
  })

  it('uma releitura em segundo plano não troca a versão sobre a qual a edição foi feita', async () => {
    const backend = useProfileBackend({ displayName: 'Ana', bio: null, version: 1 })
    const { user, queryClient } = await renderApp('/conta/perfil')
    await screen.findByLabelText('Nome de exibição')

    backend.changeElsewhere({ displayName: 'Outra aba' })
    await queryClient.invalidateQueries({ queryKey: profileQueryKey(ME.id) })
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    expect(await screen.findByText(/alterado em outra aba ou aparelho/)).toBeInTheDocument()
    expect(backend.patches[0]?.ifMatch).toBe('"1"')
  })
})

describe('perfil: conflito (412, T-19)', () => {
  it('avisa, mantém o que foi digitado, recarrega a versão atual e reaplica sobre ela', async () => {
    const backend = useProfileBackend({ displayName: 'Ana', bio: 'Oi', version: 1 })
    const { user } = await renderApp('/conta/perfil')
    const name = await screen.findByLabelText('Nome de exibição')

    backend.changeElsewhere({ bio: 'Escrito na outra aba' })
    await user.clear(name)
    await user.type(name, 'Ana Souza')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('alterado em outra aba ou aparelho')
    expect(name).toHaveValue('Ana Souza')

    await user.click(screen.getByRole('button', { name: 'Recarregar versão atual' }))

    expect(await screen.findByText('Escrito na outra aba')).toBeInTheDocument()
    expect(name).toHaveValue('Ana Souza')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    expect(await screen.findByText('Perfil salvo.')).toBeInTheDocument()
    expect(backend.patches.map((patch) => patch.ifMatch)).toEqual(['"1"', '"2"'])
  })

  it('reaplicar manda só o campo editado e não desfaz o que a outra aba mudou', async () => {
    const backend = useProfileBackend({ displayName: 'Ana', bio: 'Oi', version: 1 })
    const { user } = await renderApp('/conta/perfil')
    const name = await screen.findByLabelText('Nome de exibição')

    backend.changeElsewhere({ bio: 'Escrito na outra aba' })
    await user.clear(name)
    await user.type(name, 'Ana Souza')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))
    await user.click(await screen.findByRole('button', { name: 'Recarregar versão atual' }))
    await screen.findByText('Escrito na outra aba')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    expect(await screen.findByText('Perfil salvo.')).toBeInTheDocument()
    expect(backend.patches.at(-1)).toEqual({ ifMatch: '"2"', body: { displayName: 'Ana Souza' } })
    expect(screen.getByLabelText('Descrição curta')).toHaveValue('Escrito na outra aba')
  })

  it('descartar troca o rascunho pela versão salva', async () => {
    const backend = useProfileBackend({ displayName: 'Ana', bio: null, version: 1 })
    const { user } = await renderApp('/conta/perfil')
    const name = await screen.findByLabelText('Nome de exibição')

    backend.changeElsewhere({ displayName: 'Ana da outra aba' })
    await user.clear(name)
    await user.type(name, 'Rascunho')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))
    await user.click(await screen.findByRole('button', { name: 'Recarregar versão atual' }))
    await user.click(await screen.findByRole('button', { name: 'Descartar minhas alterações' }))

    expect(name).toHaveValue('Ana da outra aba')
  })
})

describe('perfil: outras falhas', () => {
  it('conta restrita recebe a explicação, não um erro genérico', async () => {
    useProfileBackend({ displayName: 'Ana', bio: null, version: 1 })
    server.use(http.patch(`${API}/me/profile`, () => problem(403, 'ACCOUNT_RESTRICTED')))
    const { user } = await renderApp('/conta/perfil')

    await screen.findByLabelText('Nome de exibição')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('não permite alterar o perfil')
  })

  it('falha de rede ao salvar mostra o erro de conexão', async () => {
    useProfileBackend({ displayName: 'Ana', bio: null, version: 1 })
    server.use(http.patch(`${API}/me/profile`, () => HttpResponse.error()))
    const { user } = await renderApp('/conta/perfil')

    await screen.findByLabelText('Nome de exibição')
    await user.click(screen.getByRole('button', { name: 'Salvar perfil' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Confira sua conexão')
  })
})
