import { PrivateOperationProvider } from '@/features/session/PrivateOperationProvider'
import { installTestMe } from '@/test/private-context'
import { QueryClientProvider } from '@tanstack/react-query'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { delay, http, HttpResponse } from 'msw'
import { MemoryRouter } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { AccountContext } from '@/features/session/account-context'
import type { AccountStatus } from '@/features/session/session-api'
import { meQueryKey } from '@/features/session/query-keys'
import { API, problem, server } from '@/test/msw'
import { createTestQueryClient, renderWithI18n } from '@/test/render'
import { PhotoReviewOwnerIndex } from './PhotoReviewOwnerIndex'

const PHOTOS = `${API}/me/photos`
const PHOTO = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
const NEW_PHOTO = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc'
const TAG_V1 = '"photo-bbbbbbbb-1"'
const TAG_V2 = '"photo-bbbbbbbb-2"'
const CANARY = '<img src=x onerror="window.CANARY=1">CANARY storage/internal/key'

const stored = {
  photoId: PHOTO,
  status: 'AwaitingReview',
  rejectionCode: null,
  createdAt: '2026-10-25T10:00:00+00:00',
}

interface MountOptions {
  readonly status?: AccountStatus
  readonly locale?: 'pt-BR' | 'en-US'
}

function mount({ status = 'Active', locale = 'pt-BR' }: MountOptions = {}) {
  const me = installTestMe({
    id: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd',
    email: 'owner@example.test',
    status,
    emailConfirmed: true,
    mfaEnabled: false,
    roles: ['participant'],
  })
  const client = createTestQueryClient()
  const view = renderWithI18n(
    <QueryClientProvider client={client}>
      <AccountContext value={me}>
        <PrivateOperationProvider owner={me.owner}>
          <MemoryRouter>
            <PhotoReviewOwnerIndex />
          </MemoryRouter>
        </PrivateOperationProvider>
      </AccountContext>
    </QueryClientProvider>,
    locale,
  )
  return { ...view, client }
}

function listing(...items: readonly object[]): HttpResponse<string> {
  return new HttpResponse(JSON.stringify({ items, nextCursor: null }), {
    headers: { 'Content-Type': 'application/json' },
  })
}

function useList(...items: readonly object[]): void {
  server.use(http.get(PHOTOS, () => listing(...items)))
}

function jpeg(size = 3): File {
  return new File([new Uint8Array(size).fill(7)], 'synthetic.jpg', { type: 'image/jpeg' })
}

interface SentUpload {
  readonly key: string | null
  readonly type: string | null
  readonly bytes: readonly number[]
}

/** Registra cada POST e responde com a próxima resposta da fila (a última se repete). */
function usePosts(...answers: readonly (() => Response | Promise<Response>)[]): SentUpload[] {
  const sent: SentUpload[] = []
  server.use(
    http.post(PHOTOS, async ({ request }) => {
      sent.push({
        key: request.headers.get('Idempotency-Key'),
        type: request.headers.get('Content-Type'),
        bytes: [...new Uint8Array(await request.arrayBuffer())],
      })
      const answer = answers[Math.min(sent.length, answers.length) - 1]
      if (answer === undefined) throw new Error('POST sem resposta definida no teste')
      return answer()
    }),
  )
  return sent
}

const accepted = (): Response => HttpResponse.json({ photoId: NEW_PHOTO, status: 'Quarantined' }, { status: 202 })

async function sendFile(file: File = jpeg()): Promise<void> {
  const user = userEvent.setup({ applyAccept: false })
  await user.upload(await screen.findByLabelText('Foto'), file)
  await user.click(screen.getByRole('button', { name: 'Enviar foto' }))
}

function useStatus(eTag: string = TAG_V1, status = 'AwaitingReview'): void {
  server.use(
    http.get(`${PHOTOS}/${PHOTO}/status`, () =>
      HttpResponse.json({ photoId: PHOTO, status, rejectionCode: null }, { headers: { ETag: eTag } }),
    ),
  )
}

interface SentDeletion {
  readonly ifMatch: string | null
  readonly key: string | null
}

function useDeletes(...answers: readonly (() => Response)[]): SentDeletion[] {
  const sent: SentDeletion[] = []
  server.use(
    http.delete(`${PHOTOS}/${PHOTO}`, ({ request }) => {
      sent.push({ ifMatch: request.headers.get('If-Match'), key: request.headers.get('Idempotency-Key') })
      const answer = answers[Math.min(sent.length, answers.length) - 1]
      if (answer === undefined) throw new Error('DELETE sem resposta definida no teste')
      return answer()
    }),
  )
  return sent
}

const deleted = (): Response => new HttpResponse(null, { status: 204, headers: { ETag: TAG_V2 } })

async function askToDelete(): Promise<void> {
  await userEvent.click(await screen.findByRole('button', { name: `Excluir a foto ${PHOTO}` }))
}

async function confirmDeletion(): Promise<void> {
  await userEvent.click(await screen.findByRole('button', { name: 'Confirmar exclusão' }))
}

async function expectFocusedNotice(message: string): Promise<void> {
  const notice = await screen.findByText(message)
  expect(notice.closest('[role="status"]')).toHaveFocus()
}

describe('gate: a tela só abre com o upload ligado no servidor', () => {
  it('com o upload desligado não oferece envio nem exclusão e não tenta enviar nada', async () => {
    const posts = usePosts(accepted)
    server.use(http.get(PHOTOS, () => problem(503, 'MEDIA_UPLOADS_DISABLED')))
    mount()

    expect(await screen.findByText('As fotos ainda não estão disponíveis no Duora.')).toBeVisible()
    expect(screen.queryByLabelText('Foto')).not.toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
    expect(posts).toEqual([])
  })

  it('não mostra o formulário antes de o servidor responder', async () => {
    server.use(
      http.get(PHOTOS, async () => {
        await delay(30)
        return listing()
      }),
    )
    mount()

    expect(screen.getByRole('status')).toHaveTextContent('Carregando suas fotos…')
    expect(screen.queryByLabelText('Foto')).not.toBeInTheDocument()
    expect(await screen.findByLabelText('Foto')).toBeVisible()
  })

  it.each([
    ['503 transitório', () => problem(503, 'TEMPORARILY_UNAVAILABLE')],
    ['500', () => new HttpResponse(null, { status: 500 })],
    ['resposta fora do contrato', () => HttpResponse.json({ items: 'x' })],
  ])('com %s não abre o formulário e oferece ler de novo', async (_name, answer) => {
    server.use(http.get(PHOTOS, answer))
    mount()

    expect(await screen.findByRole('button', { name: 'Ler estado atual' })).toBeVisible()
    expect(screen.queryByLabelText('Foto')).not.toBeInTheDocument()
  })

  it('fecha a tela quando o upload é desligado depois de ela abrir', async () => {
    let open = true
    server.use(http.get(PHOTOS, () => (open ? listing() : problem(503, 'MEDIA_UPLOADS_DISABLED'))))
    usePosts(() => {
      open = false
      return problem(503, 'MEDIA_UPLOADS_DISABLED')
    })
    mount()

    await sendFile()

    expect(await screen.findByText('As fotos ainda não estão disponíveis no Duora.')).toBeVisible()
    expect(screen.queryByLabelText('Foto')).not.toBeInTheDocument()
  })
})

describe('quem pode enviar', () => {
  it.each(['Active', 'PendingEligibility'] as const)('conta %s vê o formulário', async (status) => {
    useList()
    mount({ status })

    expect(await screen.findByLabelText('Foto')).toBeVisible()
  })

  it.each(['Suspended', 'Restricted', 'PendingEmail', 'DeletionPending', 'Unknown'] as const)(
    'conta %s não envia, mas ainda vê e pode excluir as próprias fotos',
    async (status) => {
      useList(stored)
      mount({ status })

      expect(await screen.findByRole('button', { name: `Excluir a foto ${PHOTO}` })).toBeVisible()
      expect(screen.getByText('Esta conta não pode enviar fotos agora.')).toBeVisible()
      expect(screen.queryByLabelText('Foto')).not.toBeInTheDocument()
    },
  )
})

describe('envio', () => {
  it('manda os bytes uma vez, confirma e relê a lista', async () => {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return reads === 1 ? listing() : listing({ ...stored, photoId: NEW_PHOTO, status: 'Quarantined' })
      }),
    )
    const posts = usePosts(accepted)
    mount()

    await sendFile()

    await expectFocusedNotice('Foto recebida e enviada para análise.')
    expect(await screen.findByRole('heading', { name: `Foto ${NEW_PHOTO}` })).toBeVisible()
    expect(posts).toHaveLength(1)
    expect(posts[0]).toMatchObject({ type: 'image/jpeg', bytes: [7, 7, 7] })
    expect(posts[0]?.key).toMatch(/^[A-Za-z0-9_-]{1,64}$/)
    expect(screen.getByLabelText<HTMLInputElement>('Foto').files).toHaveLength(0)
  })

  it('duas fotos seguidas usam chaves diferentes', async () => {
    useList()
    const posts = usePosts(accepted)
    mount()

    await sendFile()
    await screen.findByText('Foto recebida e enviada para análise.')
    await sendFile()

    await waitFor(() => {
      expect(posts).toHaveLength(2)
    })
    expect(posts[0]?.key).not.toBe(posts[1]?.key)
  })

  it('pede a foto quando nenhum arquivo foi escolhido', async () => {
    useList()
    const posts = usePosts(accepted)
    mount()

    await userEvent.click(await screen.findByRole('button', { name: 'Enviar foto' }))

    expect(await screen.findByText('Escolha uma foto.')).toBeVisible()
    expect(posts).toEqual([])
  })

  it.each([
    [
      'tipo fora da lista',
      new File([new Uint8Array(3)], 'a.gif', { type: 'image/gif' }),
      'Envie uma foto em JPEG, PNG ou WebP.',
    ],
    ['arquivo vazio', jpeg(0), 'Este arquivo está vazio.'],
    ['acima de 5 MiB', jpeg(5 * 1024 * 1024 + 1), 'A foto precisa ter até 5 MB.'],
  ])('recusa %s localmente, sem gastar um envio', async (_name, file, message) => {
    useList()
    const posts = usePosts(accepted)
    mount()

    await sendFile(file)

    expect(await screen.findByText(message)).toBeVisible()
    expect(posts).toEqual([])
  })

  it.each([
    ['falha de rede', () => HttpResponse.error()],
    ['500', () => new HttpResponse(null, { status: 500 })],
    ['fila cheia', () => problem(503, 'MEDIA_QUEUE_FULL', { 'Retry-After': '60' })],
    ['pedido ainda em curso', () => problem(409, 'IDEMPOTENCY_REQUEST_IN_PROGRESS', { 'Retry-After': '5' })],
    ['recibo fora do contrato', () => HttpResponse.json({ photoId: 'x' }, { status: 202 })],
  ])('com resultado incerto (%s) repete a mesma chave e os mesmos bytes', async (_name, failure) => {
    useList()
    const posts = usePosts(failure, accepted)
    mount()

    await sendFile()
    await userEvent.click(await screen.findByRole('button', { name: 'Repetir o mesmo pedido' }))

    await screen.findByText('Foto recebida e enviada para análise.')
    expect(posts).toHaveLength(2)
    expect(posts[1]).toEqual(posts[0])
  })

  it('não deixa trocar de arquivo enquanto o resultado é incerto', async () => {
    useList()
    usePosts(() => new HttpResponse(null, { status: 500 }))
    mount()

    await sendFile()

    await screen.findByRole('button', { name: 'Repetir o mesmo pedido' })
    expect(screen.getByLabelText('Foto')).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'Enviar foto' })).not.toBeInTheDocument()
  })

  it.each([
    [
      'limite de pendentes',
      () => problem(429, 'UPLOAD_PENDING_LIMIT', { 'Retry-After': '60' }),
      'Você atingiu o limite de fotos em análise. Aguarde o resultado ou exclua uma foto.',
    ],
    ['conta restrita', () => problem(403, 'ACCOUNT_RESTRICTED'), 'Esta conta não pode enviar fotos agora.'],
    ['arquivo grande na borda', () => problem(413, 'PAYLOAD_TOO_LARGE'), 'A foto precisa ter até 5 MB.'],
    ['tipo recusado', () => problem(415, 'UNSUPPORTED_MEDIA_TYPE'), 'Envie uma foto em JPEG, PNG ou WebP.'],
    [
      'envio interrompido',
      () => problem(400, 'UPLOAD_INCOMPLETE'),
      'O envio foi interrompido. Escolha a foto e tente de novo.',
    ],
  ])('recusa definitiva (%s) explica e permite escolher outra foto com chave nova', async (_name, refusal, message) => {
    useList()
    const posts = usePosts(refusal, accepted)
    mount()

    await sendFile()

    expect(await screen.findByText(message)).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Repetir o mesmo pedido' })).not.toBeInTheDocument()
    await sendFile()
    await screen.findByText('Foto recebida e enviada para análise.')
    expect(posts[0]?.key).not.toBe(posts[1]?.key)
  })

  it('nunca mostra texto arbitrário do servidor', async () => {
    useList()
    usePosts(
      () =>
        new HttpResponse(
          JSON.stringify({ type: 'about:blank', title: CANARY, detail: CANARY, status: 400, code: CANARY }),
          {
            status: 400,
            headers: { 'Content-Type': 'application/problem+json' },
          },
        ),
    )
    const { container } = mount()

    await sendFile()

    await screen.findByRole('alert')
    expect(container).not.toHaveTextContent('CANARY')
  })

  it('401 no envio manda reconferir a identidade', async () => {
    useList()
    usePosts(() => problem(401, 'UNAUTHENTICATED'))
    const { client } = mount()
    const invalidate = vi.spyOn(client, 'invalidateQueries')

    await sendFile()

    await waitFor(() => {
      expect(invalidate).toHaveBeenCalledWith({ queryKey: meQueryKey })
    })
  })
})

describe('estado e motivo', () => {
  it.each(['Pending', 'Quarantined', 'Processing', 'AwaitingReview'])(
    '%s avisa que nada aparece para ninguém',
    async (status) => {
      useList({ ...stored, status })
      mount()

      expect(await screen.findByText('Em análise; ainda não aparece para ninguém.')).toBeVisible()
    },
  )

  it('foto aprovada não usa o aviso de análise', async () => {
    useList({ ...stored, status: 'Approved' })
    mount()

    expect(await screen.findByText('Aprovada')).toBeVisible()
    expect(screen.queryByText('Em análise; ainda não aparece para ninguém.')).not.toBeInTheDocument()
  })

  it.each([
    ['pt-BR', 'PHOTO_REVIEW_REJECTED', 'A foto não foi aprovada. Consulte as regras e envie outra foto.'],
    ['pt-BR', 'PHOTO_REVIEW_EXPIRED', 'A análise não terminou no prazo. Envie outra foto para tentar novamente.'],
    ['pt-BR', 'PHOTO_REVIEW_UNAVAILABLE', 'Esta foto não está mais disponível para análise. Envie outra foto.'],
    ['en-US', 'PHOTO_REVIEW_REJECTED', 'This photo was not approved. Check the rules and upload another photo.'],
    [
      'en-US',
      'PHOTO_REVIEW_EXPIRED',
      'The review did not finish before the deadline. Upload another photo to try again.',
    ],
    ['en-US', 'PHOTO_REVIEW_UNAVAILABLE', 'This photo is no longer available for review. Upload another photo.'],
  ] as const)('%s: %s usa a mensagem pública do contrato', async (locale, rejectionCode, message) => {
    useList({ ...stored, status: 'Rejected', rejectionCode })
    mount({ locale })

    expect(await screen.findByText(message)).toBeVisible()
  })

  it.each([
    ['UNSUPPORTED_FORMAT', 'Este formato de imagem não é aceito. Envie outra foto.'],
    ['TYPE_MISMATCH', 'O conteúdo do arquivo não corresponde ao tipo da imagem. Envie outra foto.'],
    ['TOO_MANY_PIXELS', 'A imagem tem resolução acima do limite. Envie uma foto menor.'],
    ['ANIMATED', 'Imagens animadas não são aceitas. Envie uma foto.'],
    ['CORRUPT_IMAGE', 'Não foi possível ler esta imagem. Envie outra foto.'],
    ['PROCESSING_LIMIT', 'Não foi possível processar esta imagem. Envie outra foto.'],
  ])('rejeição técnica %s tem motivo próprio', async (rejectionCode, message) => {
    useList({ ...stored, status: 'Rejected', rejectionCode })
    mount()

    expect(await screen.findByText(message)).toBeVisible()
  })

  it.each(['FUTURE_CODE', 'constructor', CANARY])(
    'código desconhecido "%s" vira a orientação genérica',
    async (rejectionCode) => {
      useList({ ...stored, status: 'Rejected', rejectionCode })
      const { container } = mount()

      expect(await screen.findByText('A foto não foi aprovada. Envie outra foto.')).toBeVisible()
      expect(container).not.toHaveTextContent(rejectionCode)
    },
  )

  it('não oferece pedido de revisão da decisão', async () => {
    useList({ ...stored, status: 'Rejected', rejectionCode: 'PHOTO_REVIEW_REJECTED' })
    mount()

    await screen.findByRole('heading', { name: `Foto ${PHOTO}` })
    expect(screen.queryByRole('button', { name: /recurso|revisão da decisão|contestar/i })).not.toBeInTheDocument()
  })

  it('relê o estado sob pedido', async () => {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return listing({ ...stored, status: reads === 1 ? 'Quarantined' : 'Approved' })
      }),
    )
    mount()

    await screen.findByText('Em quarentena')
    await userEvent.click(screen.getByRole('button', { name: 'Ler estado atual' }))

    expect(await screen.findByText('Aprovada')).toBeVisible()
  })

  it('não mostra imagem, porque esta tela não lê API-M04', async () => {
    useList({ ...stored, status: 'Approved' })
    mount()

    await screen.findByText('Aprovada')
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })
})

describe('exclusão', () => {
  it.each(['Quarantined', 'Processing', 'AwaitingReview', 'Approved', 'Rejected', 'future-state'])(
    'oferece excluir foto em %s',
    async (status) => {
      useList({ ...stored, status })
      mount()

      expect(await screen.findByRole('button', { name: `Excluir a foto ${PHOTO}` })).toBeVisible()
    },
  )

  it('não oferece excluir foto em Pending, que o servidor recusa', async () => {
    useList({ ...stored, status: 'Pending' })
    mount()

    await screen.findByText('Pendente')
    expect(screen.queryByRole('button', { name: `Excluir a foto ${PHOTO}` })).not.toBeInTheDocument()
  })

  it('só exclui depois de confirmar o estado atual, na versão lida em API-M02', async () => {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return reads === 1 ? listing(stored) : listing()
      }),
    )
    useStatus(TAG_V1, 'Approved')
    const deletes = useDeletes(deleted)
    mount()

    await askToDelete()

    expect(await screen.findByText('Estado atual: Aprovada')).toBeVisible()
    expect(screen.getByRole('group', { name: 'Excluir esta foto? Não dá para desfazer.' })).toHaveFocus()
    expect(deletes).toEqual([])

    await confirmDeletion()

    await expectFocusedNotice('Foto excluída. A remoção dos arquivos termina em seguida.')
    expect(screen.queryByRole('heading', { name: `Foto ${PHOTO}` })).not.toBeInTheDocument()
    expect(deletes).toHaveLength(1)
    expect(deletes[0]?.ifMatch).toBe(TAG_V1)
    expect(deletes[0]?.key).toMatch(/^[A-Za-z0-9_-]{1,64}$/)
  })

  it('cancelar não exclui e devolve o foco ao botão', async () => {
    useList(stored)
    useStatus()
    const deletes = useDeletes(deleted)
    mount()

    await askToDelete()
    await userEvent.click(await screen.findByRole('button', { name: 'Manter foto' }))

    expect(await screen.findByRole('button', { name: `Excluir a foto ${PHOTO}` })).toHaveFocus()
    expect(deletes).toEqual([])
  })

  it('versão vencida (412) relê e pede nova confirmação, com chave e ETag novos', async () => {
    useList(stored)
    let reads = 0
    server.use(
      http.get(`${PHOTOS}/${PHOTO}/status`, () => {
        reads += 1
        return HttpResponse.json(
          { photoId: PHOTO, status: reads === 1 ? 'AwaitingReview' : 'Approved', rejectionCode: null },
          { headers: { ETag: reads === 1 ? TAG_V1 : TAG_V2 } },
        )
      }),
    )
    const deletes = useDeletes(() => problem(412, 'PRECONDITION_FAILED'), deleted)
    mount()

    await askToDelete()
    await confirmDeletion()

    expect(await screen.findByText('Esta foto mudou. Confira o estado atual e confirme de novo.')).toBeVisible()
    await userEvent.click(screen.getByRole('button', { name: 'Ler estado atual da foto' }))
    expect(await screen.findByText('Estado atual: Aprovada')).toBeVisible()
    await confirmDeletion()

    await screen.findByText('Foto excluída. A remoção dos arquivos termina em seguida.')
    expect(deletes.map((sent) => sent.ifMatch)).toEqual([TAG_V1, TAG_V2])
    expect(deletes[0]?.key).not.toBe(deletes[1]?.key)
  })

  it('foto em Pending no servidor (409) explica sem repetir sozinha', async () => {
    useList(stored)
    useStatus()
    const deletes = useDeletes(() => problem(409, 'PHOTO_DELETE_STATE_CONFLICT'))
    mount()

    await askToDelete()
    await confirmDeletion()

    expect(await screen.findByText('Esta foto ainda está sendo recebida. Tente excluir daqui a pouco.')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Repetir o mesmo pedido' })).not.toBeInTheDocument()
    expect(deletes).toHaveLength(1)
  })

  it.each([
    ['falha de rede', () => HttpResponse.error()],
    ['500', () => new HttpResponse(null, { status: 500 })],
    ['503 transitório', () => problem(503, 'TEMPORARILY_UNAVAILABLE', { 'Retry-After': '2' })],
  ])('com resultado incerto (%s) repete a mesma intenção', async (_name, failure) => {
    useList(stored)
    useStatus()
    const deletes = useDeletes(failure, deleted)
    mount()

    await askToDelete()
    await confirmDeletion()
    await userEvent.click(await screen.findByRole('button', { name: 'Repetir o mesmo pedido' }))

    await screen.findByText('Foto excluída. A remoção dos arquivos termina em seguida.')
    expect(deletes).toHaveLength(2)
    expect(deletes[1]).toEqual(deletes[0])
  })

  it('foto que sumiu (404 na leitura) sai da lista sem pedir confirmação', async () => {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return reads === 1 ? listing(stored) : listing()
      }),
      http.get(`${PHOTOS}/${PHOTO}/status`, () => problem(404, 'NOT_FOUND')),
    )
    const deletes = useDeletes(deleted)
    mount()

    await askToDelete()

    await waitFor(() => {
      expect(screen.queryByRole('heading', { name: `Foto ${PHOTO}` })).not.toBeInTheDocument()
    })
    expect(await screen.findByText('Nenhuma foto disponível nesta lista.')).toBeVisible()
    expect(deletes).toEqual([])
  })

  it('foto que sumiu (404 no DELETE) sai da lista', async () => {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return reads === 1 ? listing(stored) : listing()
      }),
    )
    useStatus()
    useDeletes(() => problem(404, 'NOT_FOUND'))
    mount()

    await askToDelete()
    await confirmDeletion()

    expect(await screen.findByText('Nenhuma foto disponível nesta lista.')).toBeVisible()
  })

  it('sem ETag válido não confirma nem envia DELETE', async () => {
    useList(stored)
    server.use(
      http.get(`${PHOTOS}/${PHOTO}/status`, () =>
        HttpResponse.json({ photoId: PHOTO, status: 'Approved', rejectionCode: null }),
      ),
    )
    const deletes = useDeletes(deleted)
    mount()

    await askToDelete()

    await screen.findByRole('alert')
    expect(screen.queryByRole('button', { name: 'Confirmar exclusão' })).not.toBeInTheDocument()
    expect(deletes).toEqual([])
  })

  it('não oferece histórico à conta suspensa, mas exclui', async () => {
    useList(stored)
    useStatus()
    const deletes = useDeletes(deleted)
    mount({ status: 'Suspended' })

    await askToDelete()
    await confirmDeletion()

    await screen.findByText('Foto excluída. A remoção dos arquivos termina em seguida.')
    expect(deletes).toHaveLength(1)
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })
})
