# Problem Details — plano de implementação

Objetivo: concluir #104 preservando status, code, type, traceId, errors, Retry-After e i18n existente.
Spec: issue #104; spec 8.1/8.6/13.6; ADR-0022/0026. INV-05/12 preservados.
Alcance: erros API-A01–A16, perfil/preferências/selfie, mídia, participação/catálogo e bloqueio.
Sem mudança em autorização, concorrência, dados, dependências ou implantação. Rollback por reversão do commit.

- [x] Contrato: reproduzir título português e detail arbitrário em testes; adicionar catálogo literal por código em ApiProblems e eliminar parâmetros de título dos produtores. Testar validação, todos os códigos e normalização do framework, incluindo fallback HTTP.
- [x] SPA: inventariar consumidores; testar RequestFailureAlert e páginas reais com canários em title/detail/errors e códigos desconhecidos, incluindo nomes herdados de Object.prototype. Preservar parser e tradução/fallback existentes.
- [x] E2E: exercitar autenticação, validação, indisponibilidade e falhas de leitura/mutação em PT-BR/en-US usando respostas interceptadas, garantindo mensagem local e ausência de canário no documento.
- [x] Regenerar OpenAPI/tipos, comparar com base, executar suites e verificações disponíveis; registrar inventário e resultados para revisão humana.

Foco de revisão: código desconhecido; mensagens internas do framework; erros por campo; caminhos de autenticação especiais; status/cabeçalhos invariantes.
Execução nesta sessão conforme solicitação de implementação; revisão humana antes de merge.
