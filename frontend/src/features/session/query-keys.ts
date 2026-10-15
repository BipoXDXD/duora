/** Consulta que descobre a identidade; o resultado dela decide as chaves dos dados privados. */
export const meQueryKey = ['me'] as const

/** Dados privados ficam sob o id do titular: duas identidades nunca dividem a mesma chave (spec 13.5). */
export function sessionsQueryKey(userId: string): readonly ['account', string, 'sessions'] {
  return ['account', userId, 'sessions']
}

export function profileQueryKey(userId: string): readonly ['account', string, 'profile'] {
  return ['account', userId, 'profile']
}
