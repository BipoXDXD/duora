import { z } from 'zod'
import { paths } from '@/shared/paths'

/** Por que a pessoa chegou à tela de entrada, quando não foi por escolha própria (spec 13.2). */
export type LoginNotice = 'session-expired' | 'sign-in-required' | 'signed-out'

export interface LoginState {
  readonly notice?: LoginNotice
  /** Caminho interno para voltar depois de entrar. */
  readonly from?: string
}

/**
 * Destinos aceitos para voltar depois de entrar: as rotas da SPA que não fazem parte do próprio
 * fluxo de entrada. Qualquer outro caminho, mesmo interno, é ignorado.
 */
const RETURN_PATHS: ReadonlySet<string> = new Set([paths.home, paths.account])

// Base fictícia só para decompor o caminho; o destino nunca sai da origem da SPA.
const PARSING_ORIGIN = 'https://duora.invalid'

// Começa com uma barra e não com duas nem com barra invertida (que o navegador lê como outra origem).
const internalPathSchema = z
  .string()
  .max(2048)
  .regex(/^\/(?![/\\])/)
  .refine((path) => RETURN_PATHS.has(new URL(path, PARSING_ORIGIN).pathname))

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
