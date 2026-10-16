import { z } from 'zod'

const CHANNEL_NAME = 'duora.identity'

/** Só o aviso e a aba de origem: nenhum dado da identidade atravessa o canal. */
const identityChangedSchema = z.object({ type: z.literal('identity-changed'), tabId: z.string() })

/**
 * Aviso entre abas da mesma origem de que a identidade do navegador mudou (entrada ou saída). As outras abas
 * descartam o que mostram e reconsultam a identidade, em vez de seguir exibindo a pessoa anterior (T-21).
 */
export interface IdentityBroadcast {
  readonly announce: () => void
  /** Devolve a função que cancela a inscrição. */
  readonly subscribe: (listener: () => void) => () => void
}

/**
 * Sem `BroadcastChannel` (navegador antigo), nada é enviado: cada aba ainda descobre a troca ao reconsultar a
 * identidade, que descarta os dados de quem saiu (query-client.ts). `tabId` só é chamado quando há canal.
 */
export function createIdentityBroadcast(tabId: () => string): IdentityBroadcast {
  return {
    announce: () => {
      if (typeof BroadcastChannel === 'undefined') {
        return
      }
      const channel = new BroadcastChannel(CHANNEL_NAME)
      channel.postMessage({ type: 'identity-changed', tabId: tabId() } satisfies z.input<typeof identityChangedSchema>)
      channel.close()
    },
    subscribe: (listener) => {
      if (typeof BroadcastChannel === 'undefined') {
        return () => undefined
      }
      const channel = new BroadcastChannel(CHANNEL_NAME)
      channel.onmessage = (event: MessageEvent<unknown>) => {
        const message = identityChangedSchema.safeParse(event.data)
        if (message.success && message.data.tabId !== tabId()) {
          listener()
        }
      }
      return () => {
        channel.close()
      }
    },
  }
}

/** Gera o id só na primeira vez que é pedido, e sempre devolve o mesmo. */
function lazyTabId(): () => string {
  let tabId: string | undefined
  return () => (tabId ??= crypto.randomUUID())
}

/**
 * Canal desta aba; o id distingue o próprio aviso, que o BroadcastChannel também entrega à mesma aba. O id só é
 * gerado quando há BroadcastChannel, porque `crypto.randomUUID` exige contexto seguro e não teria uso sem ele.
 */
export const identityBroadcast = createIdentityBroadcast(lazyTabId())
