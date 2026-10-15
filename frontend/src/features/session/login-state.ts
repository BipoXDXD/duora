import { z } from 'zod'

/** Por que a pessoa chegou à tela de entrada, quando não foi por escolha própria (spec 13.2). */
export type LoginNotice = 'session-expired' | 'sign-in-required' | 'signed-out'

export interface LoginState {
  readonly notice?: LoginNotice
  /** Caminho interno para voltar depois de entrar. */
  readonly from?: string
}

// Só caminho interno: começa com uma barra e não com duas (que o navegador lê como outra origem).
const internalPathSchema = z
  .string()
  .max(2048)
  .regex(/^\/(?![/\\])/)

const loginStateSchema = z.object({
  notice: z.enum(['session-expired', 'sign-in-required', 'signed-out']).optional(),
  from: internalPathSchema.optional(),
})

/**
 * Lê o `location.state` da rota de entrada. Ele vem do histórico do navegador, então é dado
 * externo: o que não respeitar o formato é ignorado.
 */
export function readLoginState(state: unknown): LoginState {
  const parsed = loginStateSchema.safeParse(state)
  if (!parsed.success) {
    return {}
  }
  const { notice, from } = parsed.data
  return { ...(notice === undefined ? {} : { notice }), ...(from === undefined ? {} : { from }) }
}
