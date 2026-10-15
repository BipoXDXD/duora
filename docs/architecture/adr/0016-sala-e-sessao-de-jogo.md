# ADR-0016: Sala é a sessão de jogo de um par numa rodada

- **Status:** Accepted (2026-10-15)
- **Origem:** lacuna entre spec 4.2, 8.3 e 10.2–10.3; decisão do responsável técnico
- **Revisar quando:** uma sala precisar existir sem sessão de jogo ou conter mais de uma sessão

## Contexto

A API expõe salas (`/api/v1/rooms/{roomId}/...`) e a máquina de estados da spec 4.2 descreve a sala
(`Invited`, `Ready`, `Active`, `Completed`, `Aborted`, `Expired`). O dicionário de dados da 10.2, porém, não tem
tabela `rooms`: há `pairings`/`pairing_participants` (reserva do par) e `game_sessions` (estado do jogo). A
decisão é única por `(game_session_id, user_id)` (10.3), mas é acessada por `/rooms/{roomId}/my-decision`.
Sem uma definição, cada fatia poderia modelar a sala de um jeito.

Alternativas consideradas:

- **Sala como agregado próprio**, com tabela `rooms` contendo 0..1 sessão de jogo. Descartada: cria uma
  entidade e uma máquina de estados a mais para manter sincronizadas com a sessão, sem caso de uso que exija
  sala sem sessão ou com várias sessões; a decisão continuaria presa à sessão, e a rota à sala.
- **Sala como o par (`pairing`).** Descartada: o par é a reserva da rodada e existe antes do convite ser
  aceito; misturar reserva e estado de jogo acopla Matching a Experiences.

## Decisão

Vamos tratar a sala como a `game_session` de um par numa rodada. `roomId` é o `gameSessionId`. O `pairing`
guarda a reserva do par (Matching); a sessão de jogo nasce do convite, já ligada ao `pairing` e à versão
publicada da experiência. A máquina de estados da sala da spec 4.2 é a máquina de estados da `game_session`.
A decisão continua única por `(game_session_id, user_id)`.

Porquê técnico: um identificador e uma máquina de estados por encontro, sem sincronização entre duas tabelas.
Porquê de negócio: menos modelo para construir na fatia vertical do M2.

## Consequências

- Positivas: rotas, decisão, ações e projeções compartilham o mesmo ID; versão otimista única por sala.
- Negativas: a sessão de jogo passa a carregar estados que não são de jogo (`Invited`, `Ready`, `Expired`);
  o módulo Experiences é dono do ciclo de vida da sala.
- Trade-off: se surgir sala com várias experiências em sequência, este ADR é substituído.

## Segurança e operação

Autorização de sala é autorização da sessão de jogo: participante do `pairing`, estado e bloqueio revalidados
em cada leitura e comando. `roomId` não concede acesso por ser difícil de adivinhar (spec 8.1).

## Compliance

- Migração do M2 cria `game_sessions` com referência ao `pairing` e sem tabela `rooms` (revisão de migração).
- T-02, T-04, T-05 e T-09 exercitam sala e sessão pelo mesmo ID (M2).
- Glossário em [scope.md](../../product/scope.md) e propriedade em [modules.md](../modules.md) refletem a
  decisão.
