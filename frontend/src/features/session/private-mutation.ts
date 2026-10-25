import { useMutation, type UseMutationResult } from '@tanstack/react-query'
import type { OperationHandle, SessionContextStore } from '@/shared/session-context'
import { usePrivateOperation } from './private-operation'

export interface OwnedMutationOptions<TData, TVariables> {
  readonly mutationFn: (operation: OperationHandle, variables: TVariables) => Promise<TData>
  readonly onSuccess?: (data: TData, variables: TVariables) => unknown
  readonly onError?: (error: Error, variables: TVariables) => unknown
  readonly gcTime?: number
}

export type PrivateMutationResult<TData, TVariables> = Omit<
  UseMutationResult<TData, Error, TVariables>,
  'variables' | 'mutate' | 'mutateAsync'
> & {
  readonly variables: TVariables | undefined
  readonly mutate: (value: TVariables) => void
  readonly mutateAsync: (value: TVariables) => Promise<TData>
  readonly mutateWithOperation: (operation: OperationHandle, value: TVariables) => Promise<TData>
}

/** Captura no gesto, antes de o TanStack agendar a função; callbacks conservam a mesma capacidade. */
export function usePrivateMutation<TData, TVariables = void>(
  options: OwnedMutationOptions<TData, TVariables>,
): PrivateMutationResult<TData, TVariables> {
  const privateOperation = usePrivateOperation()
  return useOwnedMutation(options, { store: privateOperation.store, begin: () => privateOperation.begin() })
}

export function useOwnedMutation<TData, TVariables = void>(
  options: OwnedMutationOptions<TData, TVariables>,
  scope: { readonly store: SessionContextStore; readonly begin: () => OperationHandle },
): PrivateMutationResult<TData, TVariables> {
  const { store } = scope
  const mutation = useMutation<
    TData,
    Error,
    { readonly value: TVariables; readonly operation: OperationHandle; readonly owned: boolean }
  >({
    ...(options.gcTime === undefined ? {} : { gcTime: options.gcTime }),
    mutationFn: async ({ value, operation }) => {
      store.assertCurrent(operation)
      const result = await options.mutationFn(operation, value)
      store.assertCurrent(operation)
      return result
    },
    onSuccess: async (data, { value, operation }) => {
      store.assertCurrent(operation)
      await options.onSuccess?.(data, value)
    },
    onError: async (error, { value, operation }) => {
      try {
        store.assertCurrent(operation)
      } catch {
        return
      }
      await options.onError?.(error, value)
    },
    onSettled: (_data, _error, { operation, owned }) => {
      if (owned) store.finish(operation)
    },
  })
  return {
    ...mutation,
    variables: mutation.variables?.value,
    mutate: (value: TVariables): void => {
      const operation = scope.begin()
      mutation.mutate({ value, operation, owned: true })
    },
    mutateAsync: (value: TVariables): Promise<TData> => {
      const operation = scope.begin()
      return mutation.mutateAsync({ value, operation, owned: true })
    },
    /** O formulário já capturou antes do resolver assíncrono e mantém a finalização da intenção. */
    mutateWithOperation: (operation: OperationHandle, value: TVariables): Promise<TData> => {
      store.assertCurrent(operation)
      return mutation.mutateAsync({ value, operation, owned: false })
    },
  }
}
