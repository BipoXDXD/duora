import { useEffect, useState } from 'react'
import { useQuery, type UseQueryResult } from '@tanstack/react-query'
import { z } from 'zod'
import { apiClient, ApiResponseError, sendMutation, sendMutationForJson } from '@/shared/api'
import type { OperationHandle } from '@/shared/session-context'
import { runPublicOperation } from '@/features/session/public-operation'
import type { Registration } from './auth-api'

const optionsSchema = z.object({ enabled: z.boolean(), registrationEnabled: z.boolean(), linkingEnabled: z.boolean() })
const flowSchema = z.object({
  nextStep: z.enum(['Login', 'Mfa', 'Register', 'ConfirmEmail', 'Link', 'Unknown', 'Failed', 'Complete']),
  expiresAt: z.iso.datetime(),
  email: z.email().nullable().optional(),
})
export type GoogleFlow = z.output<typeof flowSchema>
export type GoogleOptions = z.output<typeof optionsSchema>
const externalLoginSchema = z.object({ provider: z.literal('Google'), linkedAt: z.iso.datetime() })
export type ExternalLogin = z.output<typeof externalLoginSchema>

interface GoogleOptionsState {
  readonly data: GoogleOptions | undefined
  readonly isPending: boolean
  readonly isError: boolean
}

/** Public capability flags stay local so entering the sign-in page leaves the query cache empty. */
export function useGoogleOptions(): GoogleOptionsState {
  const [state, setState] = useState<GoogleOptionsState>({ data: undefined, isPending: true, isError: false })
  useEffect(() => {
    const controller = new AbortController()
    let mounted = true
    // Sign-in clears the old identity in its mount effect; read flags after that commit's effects.
    void Promise.resolve()
      .then(() => {
        controller.signal.throwIfAborted()
        return runPublicOperation(async (operation) =>
          optionsSchema.parse(
            await apiClient.requestJson({
              operation,
              method: 'GET',
              path: '/api/v1/auth/google/options',
              signal: controller.signal,
            }),
          ),
        )
      })
      .then(
        (data) => {
          if (mounted) setState({ data, isPending: false, isError: false })
        },
        () => {
          if (mounted) setState({ data: undefined, isPending: false, isError: true })
        },
      )
    return () => {
      mounted = false
      controller.abort()
    }
  }, [])
  return state
}
export function useGoogleFlow(enabled: boolean): UseQueryResult<GoogleFlow> {
  return useQuery({
    queryKey: ['google-flow'],
    enabled,
    retry: false,
    staleTime: 0,
    gcTime: 0,
    queryFn: ({ signal }) =>
      runPublicOperation(async (operation) =>
        flowSchema.parse(
          await apiClient.requestJson({ operation, method: 'GET', path: '/api/v1/auth/google/flow', signal }),
        ),
      ),
  })
}

/** Expiration removes completion controls even while the person is still entering a code. */
export function useGoogleFlowExpired(flow: GoogleFlow | undefined): boolean {
  const [clock, setClock] = useState(Date.now)
  useEffect(() => {
    if (!flow) return
    const remaining = Date.parse(flow.expiresAt) - Date.now()
    const timer = window.setTimeout(
      () => {
        setClock(Date.now())
      },
      Math.max(0, Math.min(remaining + 1, 2147483647)),
    )
    return () => {
      window.clearTimeout(timer)
    }
  }, [flow])
  return flow !== undefined && Date.parse(flow.expiresAt) <= clock
}

/** Only the same-origin backend challenge can initiate the provider redirect. */
export async function requestGoogle(
  operation: OperationHandle,
  purpose: 'Login' | 'Register' | 'Link',
): Promise<string> {
  const response = await sendMutationForJson({
    operation,
    method: 'POST',
    path: purpose === 'Link' ? '/api/v1/me/external-logins/google/requests' : '/api/v1/auth/google/requests',
    body: purpose === 'Link' ? {} : { purpose },
  })
  const result = z.object({ authorizationUrl: z.literal('/api/v1/auth/google/authorize') }).safeParse(response)
  if (!result.success) throw new ApiResponseError(200, 'Invalid authorization URL')
  return result.data.authorizationUrl
}
export function completeGoogle(
  operation: OperationHandle,
  factor: { twoFactorCode?: string | undefined; recoveryCode?: string | undefined },
): Promise<void> {
  return sendMutation({ operation, method: 'POST', path: '/api/v1/auth/google/completions', body: factor })
}
export function registerGoogle(operation: OperationHandle, registration: Registration): Promise<void> {
  return sendMutation({ operation, method: 'POST', path: '/api/v1/auth/google/registrations', body: registration })
}
export function confirmGoogleEmail(operation: OperationHandle, code: string, password: string): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/auth/google/registration-confirmations',
    body: { code, password },
  })
}
export async function fetchExternalLogins(
  operation: OperationHandle,
  signal: AbortSignal,
): Promise<readonly ExternalLogin[]> {
  const response = await apiClient.requestJson({ operation, method: 'GET', path: '/api/v1/me/external-logins', signal })
  return z.object({ items: z.array(externalLoginSchema) }).parse(response).items
}
export function confirmGoogleLink(operation: OperationHandle): Promise<void> {
  return sendMutation({ operation, method: 'POST', path: '/api/v1/me/external-logins/google/confirmations', body: {} })
}
export function removeGoogleLink(operation: OperationHandle): Promise<void> {
  return sendMutation({ operation, method: 'DELETE', path: '/api/v1/me/external-logins/google' })
}
export function reauthenticateGoogleSettings(
  operation: OperationHandle,
  password: string,
  twoFactorCode?: string,
): Promise<void> {
  return sendMutation({
    operation,
    method: 'POST',
    path: '/api/v1/auth/reauthenticate',
    body: { password, ...(twoFactorCode ? { twoFactorCode } : {}) },
  })
}
