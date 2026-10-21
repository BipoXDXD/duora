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
  // Authentication credentials never belong in a login return or its history state.
  for (const key of [...url.searchParams.keys()]) {
    if (/^(token|userid|code|password)$/i.test(key)) url.searchParams.delete(key)
  }
  const hash = /^#[a-zA-Z][a-zA-Z0-9_-]*$/.test(url.hash) ? url.hash : ''
  return `${pathname}${url.search}${hash}`
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
