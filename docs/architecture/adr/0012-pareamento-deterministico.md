# ADR-0012: Pareamento determinístico, com regras determinísticas

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 1.1, 5 e 23.1 (ADR-012)
- **Revisar quando:** houver evidência de benefício com avaliação formal de risco

## Contexto

Pareamento decide quem encontra quem. Erros aqui expõem pessoas a quem as bloqueou, revelam preferências ou
criam ranking de atratividade. O resultado precisa ser reproduzível para investigar incidentes e não pode
inferir atributos sensíveis.

Alternativas consideradas:

- **Recomendação por processamento externo ou ranking de atratividade.** Fora da primeira entrega (spec 1.1): opaco, não
  reproduzível e com risco de inferir gênero, orientação ou "qualidade do perfil".
- **Matching de cardinalidade máxima com restrições.** Adiado: avaliar se a heurística deixar pessoas
  elegíveis sem par de forma recorrente (spec 5.1).

## Decisão

Vamos parear por heurística determinística sobre arestas bilateralmente elegíveis. Bloqueios, sanções,
incompatibilidade declarada, repetição no evento e ocupação de outra sala eliminam a aresta antes de qualquer
pontuação. A prioridade favorece quem ficou sem par e quem espera há mais tempo; o desempate usa semente
persistida por rodada e versão do algoritmo. A reserva acontece em transação com guardas, revalidação e
unicidades.

Porquê técnico: resultado reproduzível e testável com semente injetada. Porquê de negócio: previsibilidade,
privacidade e auditabilidade.

## Consequências

- Positivas: testes determinísticos; nenhuma chamada externa no caminho do pareamento.
- Negativas: heurística gulosa não garante o máximo global de pares; a taxa de não pareamento precisa ser
  registrada.
- Trade-off: nunca relaxar bloqueio ou compatibilidade para melhorar uma métrica.

## Segurança e operação

Não expor a outra pessoa o motivo de um par não formado. Métricas agregadas por evento e versão do algoritmo,
com supressão de grupos pequenos (spec 5.3).

## Compliance

- Testes unitários com semente fixa e `TimeProvider` de teste (M2).
- T-02 (dois coordenadores reservando a mesma pessoa) e T-03 (bloqueio concorrente com pareamento): M2.
- Teste de arquitetura: o módulo Matching não referencia cliente de processamento externo (a implementar no M2).
