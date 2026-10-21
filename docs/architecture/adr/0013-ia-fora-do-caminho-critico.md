# ADR-0013: IA fora do caminho crítico das partidas

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 1.1, 6.1, 6.4, 12.2, 12.3 e 23.1 (ADR-013)
- **Revisar quando:** um caso concreto for aprovado com fallback equivalente

## Contexto

A preparação de cartões, instruções e variações ocorre antes da publicação. Uma
partida em andamento precisa de regras estáveis (INV-09) e não pode depender da disponibilidade de um
fornecedor. Dados íntimos não podem ser enviados a agentes de criação de conteúdo.

Alternativas consideradas:

- **Geração ao vivo de conteúdo durante a partida.** Fora da primeira entrega (spec 1.1).
- **Estimativa de idade com prompt num modelo genérico de visão.** Proibida (spec 12.2).
- **Decisão de moderação por classificador.** Score não é prova; sanção permanente e recurso exigem humano
  (spec 12.3).

## Decisão

Propostas de conteúdo são preparadas fora do fluxo ao vivo e passam por validação de schema, comprimento,
linguagem, conteúdo proibido, direitos e acessibilidade e por aprovação editorial antes de publicar uma versão
imutável. `CreateSession` não consulta LLM. Moderação assistida (`IContentReviewProvider`) é condicional e
recebe só o estritamente necessário.

Porquê técnico: disponibilidade e regras estáveis por versão publicada. Porquê de negócio: o jogo funciona com
o fornecedor de IA indisponível, e dados íntimos ficam fora de terceiros.

## Consequências

- Positivas: partidas reproduzíveis; catálogo com origem, modelo, revisão humana, licença e hash.
- Negativas: conteúdo novo depende de revisão editorial humana.
- Trade-off: menos variedade ao vivo em troca de previsibilidade.

## Segurança e operação

Conversas, fotos, preferências e denúncias reais nunca vão a agentes de criação de conteúdo. Fornecedor externo
de moderação exige política de dados, retenção e acesso própria antes de receber material real (spec 12.3).

## Compliance

- T-09 (versão nova do catálogo durante a partida): M2.
- Teste de arquitetura: o módulo Experiences não referencia cliente de IA (a implementar no M2).
- Teste de integração da partida com o fornecedor de IA ausente da composição (M2).

Redação sobre preparação editorial atualizada por solicitação do responsável em 21/10/2026; ver ADR-0032. As restrições de runtime e moderação permanecem.
