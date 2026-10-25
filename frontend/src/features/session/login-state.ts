import { z } from 'zod'
import { legacyPaths, paths } from '@/shared/paths'

/** Por que a pessoa chegou à tela de entrada (spec 13.2). */
export type LoginNotice = 'session-expired' | 'sign-in-required' | 'signed-out'
export interface LoginState {
  readonly notice?: LoginNotice
  readonly from?: string
}

const RETURN_PATHS: ReadonlySet<string> = new Set([
  paths.home,
  paths.account,
  paths.profile,
  paths.preferences,
  paths.selfie,
  paths.events,
  paths.mfaEnrollment,
  paths.changePassword,
])
const PARSING_ORIGIN = 'https://duora.invalid'
const ROOM_PATH = /^\/rooms\/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const RETURN_VIEWS: ReadonlyMap<string, ReadonlySet<string>> = new Map([
  ['tab', new Set(['sessions', 'photos', 'security'])],
  ['aba', new Set(['sessoes', 'fotos', 'seguranca'])],
])
const RETURN_ANCHORS: ReadonlySet<string> = new Set(['#section', '#sessions', '#photos', '#security'])

/** Never return the unparsed input: browser URL normalization must not change the origin. */
function safeReturnPath(value: string): string | null {
  if (
    !/^\/(?![/\\])/.test(value) ||
    Array.from(value).some((char) => char === '\\' || char.charCodeAt(0) <= 32 || char.charCodeAt(0) === 127)
  )
    return null
  const url = new URL(value, PARSING_ORIGIN)
  if (url.origin !== PARSING_ORIGIN) return null
  const pathname = legacyPaths[url.pathname] ?? url.pathname
  if (!RETURN_PATHS.has(pathname) && !ROOM_PATH.test(pathname)) return null
  // History state retains navigation choices, never arbitrary query values or fragments.
  const navigation = new URLSearchParams()
  for (const [key, allowed] of RETURN_VIEWS) {
    const selectedView = url.searchParams.get(key)
    if (selectedView !== null && allowed.has(selectedView)) navigation.set(key, selectedView)
  }
  const query = navigation.toString()
  const hash = RETURN_ANCHORS.has(url.hash) ? url.hash : ''
  return `${pathname}${query === '' ? '' : `?${query}`}${hash}`
}

const internalPathSchema = z
  .string()
  .max(2048)
  .transform(safeReturnPath)
  .refine((path) => path !== null)
const loginStateSchema = z.object({
  notice: z.enum(['session-expired', 'sign-in-required', 'signed-out']).optional(),
  from: internalPathSchema.optional(),
})

/** History state is untrusted; only known internal routes can be returned to. */
export function readLoginState(state: unknown): LoginState {
  const parsed = loginStateSchema.safeParse(state)
  if (!parsed.success) return {}
  const { notice, from } = parsed.data
  return { ...(notice === undefined ? {} : { notice }), ...(from == null ? {} : { from }) }
}
