/**
 * O código de uma tela não pôde ser baixado. Quase sempre é um deploy novo: o chunk que esta aba conhece
 * saiu do servidor, e o navegador guarda a falha do módulo, então só recarregar a página resolve.
 */
export class ChunkLoadError extends Error {
  constructor(cause: unknown) {
    super('Não foi possível baixar o código da tela.', { cause })
    this.name = 'ChunkLoadError'
  }
}
