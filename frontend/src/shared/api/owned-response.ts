import { contextAbort } from '../session-context'

function guardedBody(
  body: ReadableStream<Uint8Array> | null,
  assertCurrent: () => void,
  signal: AbortSignal,
): ReadableStream<Uint8Array> | null {
  if (body === null) return null
  const reader = body.getReader()
  let ended = false
  const hasEnded = (): boolean => ended
  let streamController: ReadableStreamDefaultController<Uint8Array>
  const cleanup = (): void => {
    signal.removeEventListener('abort', abort)
  }
  const fail = (error: unknown): void => {
    if (ended) return
    ended = true
    cleanup()
    streamController.error(error)
    void reader
      .cancel(error)
      .catch(() => undefined)
      .finally(() => {
        reader.releaseLock()
      })
  }
  const abort = (): void => {
    fail(contextAbort())
  }
  return new ReadableStream<Uint8Array>(
    {
      start(controller) {
        streamController = controller
        signal.addEventListener('abort', abort, { once: true })
        if (signal.aborted) abort()
      },
      async pull(controller) {
        if (ended) return
        try {
          assertCurrent()
          const chunk = await reader.read()
          assertCurrent()
          // O cancelamento pode ocorrer enquanto reader.read() está suspenso.
          if (hasEnded()) return
          if (chunk.done) {
            ended = true
            cleanup()
            reader.releaseLock()
            controller.close()
          } else controller.enqueue(chunk.value)
        } catch (error) {
          fail(error)
        }
      },
      async cancel(reason) {
        if (ended) return
        ended = true
        cleanup()
        await reader.cancel(reason)
        reader.releaseLock()
      },
    },
    { highWaterMark: 0 },
  )
}

interface ResponseMetadata {
  readonly url: string
  readonly redirected: boolean
  readonly type: ResponseType
}

/** A capacidade continua sendo verificada após o parsing, inclusive em clones e fetch gerado. */
export class OwnedResponse extends Response {
  readonly #assertCurrent: () => void
  readonly #signal: AbortSignal
  readonly #metadata: ResponseMetadata

  constructor(
    response: Response,
    ownership: { readonly assertCurrent: () => void; readonly signal: AbortSignal },
    metadata: ResponseMetadata = response,
  ) {
    // Browsers can expose a stream for a no-content response; Response's constructor rejects it.
    const nullBody = response.status === 204 || response.status === 205 || response.status === 304
    const body = response.body
    super(nullBody ? null : guardedBody(body, ownership.assertCurrent, ownership.signal), {
      status: response.status,
      statusText: response.statusText,
      headers: response.headers,
    })
    if (nullBody && body !== null) void body.cancel().catch(() => undefined)
    this.#assertCurrent = ownership.assertCurrent
    this.#signal = ownership.signal
    this.#metadata = { url: metadata.url, redirected: metadata.redirected, type: metadata.type }
  }

  override get url(): string {
    return this.#metadata.url
  }
  override get redirected(): boolean {
    return this.#metadata.redirected
  }
  override get type(): ResponseType {
    return this.#metadata.type
  }

  async #read<T>(read: () => Promise<T>): Promise<T> {
    this.#assertCurrent()
    try {
      const value = await read()
      this.#assertCurrent()
      return value
    } catch (error) {
      this.#assertCurrent()
      // Mensagens nativas de JSON podem incluir parte do corpo, inclusive um segredo de bootstrap.
      if (error instanceof SyntaxError) {
        // O erro nativo contém trecho sensível do corpo; preservá-lo como cause também o exporia.
        // eslint-disable-next-line preserve-caught-error
        throw new SyntaxError('JSON inválido na resposta.')
      }
      throw error
    }
  }

  override json(): Promise<unknown> {
    return this.#read(() => super.json() as Promise<unknown>)
  }
  override text(): Promise<string> {
    return this.#read(() => super.text())
  }
  override arrayBuffer(): Promise<ArrayBuffer> {
    return this.#read(() => super.arrayBuffer())
  }
  override blob(): Promise<Blob> {
    return this.#read(() => super.blob())
  }
  override formData(): Promise<FormData> {
    return this.#read(() => super.formData())
  }
  override async bytes(): Promise<Uint8Array<ArrayBuffer>> {
    return new Uint8Array(await this.arrayBuffer())
  }
  override clone(): Response {
    this.#assertCurrent()
    return new OwnedResponse(
      super.clone(),
      { assertCurrent: this.#assertCurrent, signal: this.#signal },
      this.#metadata,
    )
  }
}
