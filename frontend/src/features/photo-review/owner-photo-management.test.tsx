import { PrivateOperationProvider } from '@/features/session/PrivateOperationProvider'
import { installTestMe } from '@/test/private-context'
import { focusManager, QueryClientProvider } from '@tanstack/react-query'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { delay, http, HttpResponse } from 'msw'
import { MemoryRouter } from 'react-router'
import { afterEach, describe, expect, it, vi } from 'vitest'
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

function useDeletes(...answers: readonly (() => Response | Promise<Response>)[]): SentDeletion[] {
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
  await userEvent.click(await screen.findByRole('button', { name: `Excluir foto ${PHOTO}` }))
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

  it.each(['Suspended', 'Restricted'] as const)('conta %s não envia, mas exclui as próprias fotos', async (status) => {
    useList(stored)
    mount({ status })

    expect(await screen.findByRole('button', { name: `Excluir foto ${PHOTO}` })).toBeVisible()
    expect(screen.getByText('Esta conta não pode enviar fotos agora.')).toBeVisible()
    expect(screen.queryByLabelText('Foto')).not.toBeInTheDocument()
  })

  // PhotoOwnerAccess só aceita PendingEligibility, Active, Restricted e Suspended: as demais recebem 403 no DELETE.
  it.each(['PendingEmail', 'DeletionPending', 'Unknown'] as const)(
    'conta %s não envia nem oferece excluir',
    async (status) => {
      useList(stored)
      mount({ status })

      expect(await screen.findByText('Esta conta não pode enviar fotos agora.')).toBeVisible()
      expect(screen.queryByLabelText('Foto')).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: /Excluir foto/ })).not.toBeInTheDocument()
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

      expect(await screen.findByRole('button', { name: `Excluir foto ${PHOTO}` })).toBeVisible()
    },
  )

  it('não oferece excluir foto em Pending, que o servidor recusa', async () => {
    useList({ ...stored, status: 'Pending' })
    mount()

    await screen.findByText('Pendente')
    expect(screen.queryByRole('button', { name: `Excluir foto ${PHOTO}` })).not.toBeInTheDocument()
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

    expect(await screen.findByRole('button', { name: `Excluir foto ${PHOTO}` })).toHaveFocus()
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

const OTHER_PHOTO = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
const other = { ...stored, photoId: OTHER_PHOTO, status: 'Approved' }
const repeatButton = (): Promise<HTMLElement> => screen.findByRole('button', { name: 'Repetir o mesmo pedido' })
const serverError = (): Response => new HttpResponse(null, { status: 500 })

function deferred(): { readonly wait: Promise<void>; readonly release: () => void } {
  let release = (): void => undefined
  const wait = new Promise<void>((resolve) => {
    release = resolve
  })
  return { wait, release }
}

function leavingIsWarned(): boolean {
  const event = new Event('beforeunload', { cancelable: true })
  window.dispatchEvent(event)
  return event.defaultPrevented
}

async function repeatAndExpectSameUpload(posts: readonly SentUpload[], total = 2): Promise<void> {
  await userEvent.click(await repeatButton())
  await screen.findByText('Foto recebida e enviada para análise.')
  expect(posts).toHaveLength(total)
  expect(posts.every((sent) => sent.key === posts[0]?.key)).toBe(true)
}

afterEach(() => {
  focusManager.setFocused(undefined)
})

describe('o resultado incerto sobrevive às releituras da lista', () => {
  it('"Ler estado atual" não troca a chave do envio', async () => {
    useList(stored)
    const posts = usePosts(serverError, accepted)
    mount()

    await sendFile()
    await repeatButton()
    await userEvent.click(screen.getByRole('button', { name: 'Ler estado atual' }))

    await repeatAndExpectSameUpload(posts)
  })

  it('"Ler estado atual" não aborta um envio em curso', async () => {
    useList(stored)
    const gate = deferred()
    const posts = usePosts(async () => {
      await gate.wait
      return accepted()
    })
    mount()

    await sendFile()
    await waitFor(() => {
      expect(posts).toHaveLength(1)
    })
    await userEvent.click(screen.getByRole('button', { name: 'Ler estado atual' }))
    gate.release()

    expect(await screen.findByText('Foto recebida e enviada para análise.')).toBeVisible()
  })

  it('a paginação não troca a chave do envio', async () => {
    server.use(
      http.get(PHOTOS, ({ request }) =>
        new URL(request.url).searchParams.has('cursor')
          ? listing(other)
          : HttpResponse.json({ items: [stored], nextCursor: 'next' }),
      ),
    )
    const posts = usePosts(serverError, accepted)
    mount()

    await sendFile()
    await repeatButton()
    await userEvent.click(screen.getByRole('button', { name: 'Próxima página' }))
    await screen.findByRole('heading', { name: `Foto ${OTHER_PHOTO}` })

    await repeatAndExpectSameUpload(posts)
  })

  it('a releitura ao voltar à janela, mesmo falhando, não descarta o incerto', async () => {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return reads === 1 ? listing(stored) : serverError()
      }),
    )
    const posts = usePosts(serverError, accepted)
    mount()

    await sendFile()
    await repeatButton()
    focusManager.setFocused(false)
    focusManager.setFocused(true)
    await waitFor(() => {
      expect(reads).toBeGreaterThan(1)
    })

    await repeatAndExpectSameUpload(posts)
  })

  it('a exclusão concluída em outra foto não troca a chave do envio', async () => {
    useList(stored, other)
    useStatus()
    useDeletes(deleted)
    const posts = usePosts(serverError, accepted)
    mount()

    await sendFile()
    await repeatButton()
    await askToDelete()
    await confirmDeletion()
    await screen.findByText('Foto excluída. A remoção dos arquivos termina em seguida.')

    await repeatAndExpectSameUpload(posts)
  })

  it('a exclusão incerta sobrevive à paginação, com a mesma chave e o mesmo ETag', async () => {
    server.use(
      http.get(PHOTOS, ({ request }) =>
        new URL(request.url).searchParams.has('cursor')
          ? listing(other)
          : HttpResponse.json({ items: [stored], nextCursor: 'next' }),
      ),
    )
    useStatus()
    const deletes = useDeletes(serverError, deleted)
    mount()

    await askToDelete()
    await confirmDeletion()
    await repeatButton()
    await userEvent.click(screen.getByRole('button', { name: 'Próxima página' }))
    await screen.findByRole('heading', { name: `Foto ${OTHER_PHOTO}` })
    await userEvent.click(await repeatButton())

    await screen.findByText('Foto excluída. A remoção dos arquivos termina em seguida.')
    expect(deletes).toHaveLength(2)
    expect(deletes[1]).toEqual(deletes[0])
  })

  it('só uma exclusão fica aberta por vez', async () => {
    useList(stored, other)
    useStatus()
    mount()

    await askToDelete()
    await screen.findByRole('button', { name: 'Confirmar exclusão' })

    expect(screen.getByRole('button', { name: `Excluir foto ${OTHER_PHOTO}` })).toHaveAttribute('aria-disabled', 'true')
  })
})

describe('recusa na repetição', () => {
  it.each([
    ['rate limit', () => problem(429, 'TOO_MANY_REQUESTS', { 'Retry-After': '30' })],
    ['conta restrita', () => problem(403, 'ACCOUNT_RESTRICTED')],
    ['gate fechado', () => problem(503, 'MEDIA_UPLOADS_DISABLED')],
    ['corpo recusado na borda', () => problem(413, 'PAYLOAD_TOO_LARGE')],
  ])('%s vem antes da consulta da chave: o envio continua incerto', async (_name, refusal) => {
    useList()
    const posts = usePosts(serverError, refusal, accepted)
    mount()

    await sendFile()
    await userEvent.click(await repeatButton())
    await waitFor(() => {
      expect(posts).toHaveLength(2)
    })

    await repeatAndExpectSameUpload(posts, 3)
  })

  it.each([
    [
      'limite de pendentes',
      () => problem(429, 'UPLOAD_PENDING_LIMIT'),
      'Você atingiu o limite de fotos em análise. Aguarde o resultado ou exclua uma foto.',
    ],
    [
      'envio interrompido',
      () => problem(400, 'UPLOAD_INCOMPLETE'),
      'O envio foi interrompido. Escolha a foto e tente de novo.',
    ],
  ])('%s passou pela chave: encerra a intenção', async (_name, refusal, message) => {
    useList()
    const posts = usePosts(serverError, refusal, accepted)
    mount()

    await sendFile()
    await userEvent.click(await repeatButton())

    expect(await screen.findByText(message)).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Repetir o mesmo pedido' })).not.toBeInTheDocument()
    await sendFile()
    await screen.findByText('Foto recebida e enviada para análise.')
    expect(posts[2]?.key).not.toBe(posts[0]?.key)
  })

  it.each([
    ['rate limit', () => problem(429, 'TOO_MANY_REQUESTS', { 'Retry-After': '30' })],
    ['conta sem permissão', () => problem(403, 'FORBIDDEN')],
    ['gate fechado', () => problem(503, 'MEDIA_UPLOADS_DISABLED')],
  ])('exclusão: %s não encerra a incerteza', async (_name, refusal) => {
    useList(stored)
    useStatus()
    const deletes = useDeletes(serverError, refusal, deleted)
    mount()

    await askToDelete()
    await confirmDeletion()
    await userEvent.click(await repeatButton())
    await waitFor(() => {
      expect(deletes).toHaveLength(2)
    })
    await userEvent.click(await repeatButton())

    await screen.findByText('Foto excluída. A remoção dos arquivos termina em seguida.')
    expect(deletes).toHaveLength(3)
    expect(deletes[2]).toEqual(deletes[0])
  })

  it('exclusão: 412 na repetição prova que nada foi feito e pede nova confirmação', async () => {
    useList(stored)
    useStatus()
    const deletes = useDeletes(serverError, () => problem(412, 'PRECONDITION_FAILED'))
    mount()

    await askToDelete()
    await confirmDeletion()
    await userEvent.click(await repeatButton())

    expect(await screen.findByRole('button', { name: 'Ler estado atual da foto' })).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Repetir o mesmo pedido' })).not.toBeInTheDocument()
    expect(deletes).toHaveLength(2)
  })
})

describe('IDEMPOTENCY_KEY_REUSED', () => {
  const reused = (): Response => problem(409, 'IDEMPOTENCY_KEY_REUSED')
  const MESSAGE = 'Este pedido não pode ser repetido. Confira a lista antes de tentar de novo.'

  function countingList(...items: readonly object[]): { readonly reads: () => number } {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return listing(...items)
      }),
    )
    return { reads: () => reads }
  }

  it.each([
    ['na primeira tentativa', [reused]],
    ['na repetição', [serverError, reused]],
  ])('envio %s encerra a intenção e relê a lista', async (_name, answers) => {
    const list = countingList()
    usePosts(...answers)
    mount()

    await sendFile()
    if (answers.length > 1) await userEvent.click(await repeatButton())

    expect(await screen.findByText(MESSAGE)).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Repetir o mesmo pedido' })).not.toBeInTheDocument()
    await waitFor(() => {
      expect(list.reads()).toBeGreaterThan(1)
    })
  })

  it('exclusão encerra a intenção e relê a lista', async () => {
    const list = countingList(stored)
    useStatus()
    useDeletes(reused)
    mount()

    await askToDelete()
    await confirmDeletion()

    expect(await screen.findByText(MESSAGE)).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Repetir o mesmo pedido' })).not.toBeInTheDocument()
    await waitFor(() => {
      expect(list.reads()).toBeGreaterThan(1)
    })
  })
})

describe('gate fechado durante a exclusão', () => {
  function closingList(): { readonly close: () => void } {
    let open = true
    server.use(http.get(PHOTOS, () => (open ? listing(stored) : problem(503, 'MEDIA_UPLOADS_DISABLED'))))
    return {
      close: () => {
        open = false
      },
    }
  }

  it('503 do gate em API-M02 fecha a tela e não pergunta', async () => {
    const list = closingList()
    server.use(
      http.get(`${PHOTOS}/${PHOTO}/status`, () => {
        list.close()
        return problem(503, 'MEDIA_UPLOADS_DISABLED')
      }),
    )
    const deletes = useDeletes(deleted)
    mount()

    await askToDelete()

    expect(await screen.findByText('As fotos ainda não estão disponíveis no Duora.')).toBeVisible()
    expect(screen.queryByRole('button', { name: 'Confirmar exclusão' })).not.toBeInTheDocument()
    expect(deletes).toEqual([])
  })

  it('503 do gate em API-M03 fecha a tela sem oferecer repetição', async () => {
    const list = closingList()
    useStatus()
    useDeletes(() => {
      list.close()
      return problem(503, 'MEDIA_UPLOADS_DISABLED')
    })
    mount()

    await askToDelete()
    await confirmDeletion()

    expect(await screen.findByText('As fotos ainda não estão disponíveis no Duora.')).toBeVisible()
    expect(screen.queryByRole('group')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Repetir o mesmo pedido' })).not.toBeInTheDocument()
  })

  it('com exclusão incerta, o gate fechado mantém a repetição disponível', async () => {
    const list = closingList()
    useStatus()
    const deletes = useDeletes(
      serverError,
      () => {
        list.close()
        return problem(503, 'MEDIA_UPLOADS_DISABLED')
      },
      deleted,
    )
    mount()

    await askToDelete()
    await confirmDeletion()
    await userEvent.click(await repeatButton())

    await screen.findByText('As fotos ainda não estão disponíveis no Duora.')
    await userEvent.click(await repeatButton())
    await screen.findByText('Foto excluída. A remoção dos arquivos termina em seguida.')
    expect(deletes[2]).toEqual(deletes[0])
  })
})

describe('aviso ao sair da página', () => {
  it('não avisa sem nada pendente', async () => {
    useList()
    mount()

    await screen.findByLabelText('Foto')
    expect(leavingIsWarned()).toBe(false)
  })

  it('avisa durante o envio e para quando ele é confirmado', async () => {
    useList()
    const gate = deferred()
    const posts = usePosts(async () => {
      await gate.wait
      return accepted()
    })
    mount()

    await sendFile()
    await waitFor(() => {
      expect(posts).toHaveLength(1)
    })
    expect(leavingIsWarned()).toBe(true)
    gate.release()
    await screen.findByText('Foto recebida e enviada para análise.')
    expect(leavingIsWarned()).toBe(false)
  })

  it('avisa com envio incerto', async () => {
    useList()
    usePosts(serverError)
    mount()

    await sendFile()
    await repeatButton()

    expect(leavingIsWarned()).toBe(true)
  })

  it('não avisa depois de uma recusa definitiva', async () => {
    useList()
    usePosts(() => problem(403, 'ACCOUNT_RESTRICTED'))
    mount()

    await sendFile()
    await screen.findByRole('alert')

    expect(leavingIsWarned()).toBe(false)
  })

  it('avisa com exclusão incerta', async () => {
    useList(stored)
    useStatus()
    useDeletes(serverError)
    mount()

    await askToDelete()
    await confirmDeletion()
    await repeatButton()

    expect(leavingIsWarned()).toBe(true)
  })
})

describe('foco quando o controle focado some', () => {
  it('repetir o envio mantém o foco no botão, que muda de texto', async () => {
    useList()
    const gate = deferred()
    usePosts(serverError, async () => {
      await gate.wait
      return accepted()
    })
    mount()

    await sendFile()
    await userEvent.click(await repeatButton())

    const sending = await screen.findByRole('button', { name: 'Enviando…' })
    expect(sending).toHaveFocus()
    gate.release()
    await screen.findByText('Foto recebida e enviada para análise.')
  })

  it('recusa da exclusão leva o foco ao painel de confirmação', async () => {
    useList(stored)
    useStatus()
    useDeletes(() => problem(409, 'PHOTO_DELETE_STATE_CONFLICT'))
    mount()

    await askToDelete()
    await confirmDeletion()

    await screen.findByText('Esta foto ainda está sendo recebida. Tente excluir daqui a pouco.')
    expect(screen.getByRole('group', { name: 'Excluir esta foto? Não dá para desfazer.' })).toHaveFocus()
  })

  it('repetir a exclusão deixa o foco no painel', async () => {
    useList(stored)
    useStatus()
    const gate = deferred()
    useDeletes(serverError, async () => {
      await gate.wait
      return deleted()
    })
    mount()

    await askToDelete()
    await confirmDeletion()
    await userEvent.click(await repeatButton())

    expect(screen.getByRole('group', { name: 'Excluir esta foto? Não dá para desfazer.' })).toHaveFocus()
    gate.release()
    await screen.findByText('Foto excluída. A remoção dos arquivos termina em seguida.')
  })

  it('trocar de página leva o foco ao título da lista', async () => {
    server.use(
      http.get(PHOTOS, ({ request }) =>
        new URL(request.url).searchParams.has('cursor')
          ? listing(other)
          : HttpResponse.json({ items: [stored], nextCursor: 'next' }),
      ),
    )
    mount()

    await userEvent.click(await screen.findByRole('button', { name: 'Próxima página' }))
    await screen.findByRole('heading', { name: `Foto ${OTHER_PHOTO}` })
    expect(screen.getByRole('heading', { name: 'Suas fotos' })).toHaveFocus()

    await userEvent.click(screen.getByRole('button', { name: 'Primeira página' }))
    await screen.findByRole('heading', { name: `Foto ${PHOTO}` })
    expect(screen.getByRole('heading', { name: 'Suas fotos' })).toHaveFocus()
  })

  it('"Ler estado atual" mantém o foco no botão mesmo quando a leitura falha', async () => {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return reads === 1 ? listing(stored) : serverError()
      }),
    )
    mount()

    await userEvent.click(await screen.findByRole('button', { name: 'Ler estado atual' }))

    await screen.findByRole('alert')
    expect(screen.getByRole('button', { name: 'Ler estado atual' })).toHaveFocus()
  })

  it('gate fechado leva o foco ao aviso de indisponível', async () => {
    let open = true
    server.use(http.get(PHOTOS, () => (open ? listing() : problem(503, 'MEDIA_UPLOADS_DISABLED'))))
    usePosts(() => {
      open = false
      return problem(503, 'MEDIA_UPLOADS_DISABLED')
    })
    mount()

    await sendFile()

    const notice = await screen.findByText('As fotos ainda não estão disponíveis no Duora.')
    expect(notice.closest('[role="status"]')).toHaveFocus()
  })

  it('manter a foto leva o foco ao título da lista quando o botão de origem sumiu', async () => {
    let reads = 0
    server.use(
      http.get(PHOTOS, () => {
        reads += 1
        return reads === 1 ? listing(stored) : listing()
      }),
    )
    useStatus()
    mount()

    await askToDelete()
    await screen.findByRole('button', { name: 'Confirmar exclusão' })
    await userEvent.click(screen.getByRole('button', { name: 'Ler estado atual' }))
    await screen.findByText('Nenhuma foto disponível nesta lista.')
    await userEvent.click(screen.getByRole('button', { name: 'Manter foto' }))

    expect(screen.getByRole('heading', { name: 'Suas fotos' })).toHaveFocus()
  })
})

describe('nome acessível', () => {
  it('o botão de excluir contém o texto visível', async () => {
    useList(stored)
    mount()

    const button = await screen.findByRole('button', { name: `Excluir foto ${PHOTO}` })
    expect(button).toHaveTextContent('Excluir foto')
    expect(button).toHaveAccessibleName(expect.stringContaining('Excluir foto'))
  })
})
