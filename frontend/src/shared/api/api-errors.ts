import type { ProblemDetails } from './problem-details'

/** Base de todos os erros que o cliente HTTP produz; cancelamento continua sendo AbortError. */
export abstract class ApiError extends Error {}

/** O servidor respondeu com erro no formato application/problem+json. */
export class ApiProblemError extends ApiError {
  override readonly name = 'ApiProblemError'
  readonly status: number
  readonly problem: ProblemDetails

  constructor(problem: ProblemDetails) {
    super(`${String(problem.status)} ${problem.title}`)
    this.status = problem.status
    this.problem = problem
  }
}

/** O servidor respondeu fora do contrato: erro sem Problem Details ou sucesso sem JSON. */
export class ApiResponseError extends ApiError {
  override readonly name = 'ApiResponseError'
  readonly status: number

  constructor(status: number, reason: string) {
    super(`Resposta inesperada da API (${String(status)}): ${reason}`)
    this.status = status
  }
}

/** A requisição não chegou a uma resposta HTTP (offline, DNS, TLS, conexão recusada). */
export class ApiNetworkError extends ApiError {
  override readonly name = 'ApiNetworkError'

  constructor(cause: unknown) {
    super('Falha de rede ao chamar a API.', { cause })
  }
}
