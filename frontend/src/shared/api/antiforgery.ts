/**
 * Contrato antiforgery com o backend ASP.NET Core: o servidor entrega o token num cookie
 * legível pelo JavaScript e exige o mesmo valor neste header em toda requisição que altera
 * estado. O cookie de autenticação continua HttpOnly e nunca é lido aqui.
 */
export const ANTIFORGERY_COOKIE_NAME = 'XSRF-TOKEN'
export const ANTIFORGERY_HEADER_NAME = 'X-XSRF-TOKEN'

export function readAntiforgeryTokenFromCookies(cookies: string): string | null {
  for (const pair of cookies.split(';')) {
    const separatorIndex = pair.indexOf('=')
    if (separatorIndex === -1) {
      continue
    }
    const name = pair.slice(0, separatorIndex).trim()
    const value = pair.slice(separatorIndex + 1).trim()
    if (name === ANTIFORGERY_COOKIE_NAME && value !== '') {
      return decodeCookieValue(value)
    }
  }
  return null
}

function decodeCookieValue(value: string): string | null {
  try {
    return decodeURIComponent(value)
  } catch {
    // Token corrompido equivale a token ausente: o servidor recusa a mutação com Problem Details.
    return null
  }
}
