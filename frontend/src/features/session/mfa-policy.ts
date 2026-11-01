/** Só o papel exclusivo de participante oferece escolha; papel ausente, novo ou misto falha fechado. */
export function isMfaOptional(roles: readonly string[]): boolean {
  return roles.length > 0 && roles.every((role) => role === 'participant')
}
