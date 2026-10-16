# ADR-0020: Persistência da sessão de jogo por snapshot público no Core

- **Status:** Accepted (2026-10-16)
- **Origem:** spec 3.4, 6.1, 6.2 (aceite "reinício preserva estado e deadline"), 10.1, 10.4 e 10.5; INV-05,
  INV-06 e INV-09; [ADR-0004](0004-dbcontext-unico-schemas-logicos.md),
  [ADR-0016](0016-sala-e-sessao-de-jogo.md), [ADR-0021](0021-jobs-duraveis-e-leases-no-postgresql.md); motor em `backend/src/Duora.Core/Experiences/`
- **Revisar quando:** uma experiência precisar consultar ou indexar o próprio estado por SQL, o snapshot de uma
  sessão passar de 32 KiB, ou o mapeamento por experiência virar a maior parte do custo de publicar uma nova

## Contexto

O motor do M2 já está na `main` como domínio puro. `GameSession<TState, TAction>` é imutável e guarda as versões
fixadas, o par, o prazo, o status, a `StateVersion`, o estado da experiência e o registro de ações aplicadas. Os
dois últimos são `internal` de propósito: o que sai para um participante é só a projeção (INV-05). O registro já
guarda, em vez do corpo da ação, o `ActionFingerprint` (SHA-256 da forma canônica v1, com valores literais fixados
em `ActionFingerprintTests`). O estado de
Rotas Improváveis (`UnlikelyRoutesState`) também tem membros internos e hoje carrega o conteúdo inteiro dentro de
`RoutesControlState`. Não existe caminho de volta do banco para a sessão: `GameSession.Start` é o único
construtor, e o `DuoraDbContext` é interno à Infrastructure, que não enxerga os internos do Core.

O [model.md](../../data/model.md#schema-experiences) já prevê `game_sessions` (`public_state`, `control_state`,
`seed`, `version`), `game_participants.private_state` e `game_actions`, e a spec 6.1 e 10.1 restringe o JSONB a
estado de jogo e payloads versionados, com schema, limite e validação. Falta decidir **como** o estado sai do
Core e volta para ele sem abrir os internos nem perder as garantias do motor.

Forças:

- **INV-05.** Pista de um participante nunca chega ao outro. Quanto mais tipos internos ficam visíveis fora do
  Core, mais fácil alguém serializar o estado inteiro numa resposta.
- **INV-09.** A sessão mantém regras e conteúdo da versão com que nasceu, mesmo que o catálogo publique outra.
- **INV-06.** Reenvio do mesmo `actionId` devolve o resultado original; mesmo `actionId` com outro corpo é
  rejeitado. Isso precisa sobreviver a reinício, então o registro de ações vai ao banco.
- **Reinício.** O aceite da 6.2 exige que reiniciar o processo durante a etapa de pistas preserve estado e prazo.
- **Evolução.** Sessões ativas sobrevivem a deploys. O formato gravado por uma release precisa ser lido pela
  seguinte (deploy sem downtime, spec 17).
- **Concorrência.** Ação e prazo competem pela mesma sala (C4 e C6 de
  [concurrency.md](../../data/concurrency.md#c4-duas-ações-de-jogo)).

Alternativas consideradas:

- **(a) Snapshot público no Core (memento), conteúdo resolvido pela versão fixada.** O Core expõe um
  `GameSessionSnapshot` de primitivos e `GameSession.Restore(snapshot, content)`. Cada experiência traduz seu
  estado para um DTO próprio, versionado por `SchemaVersion`, e de volta. O conteúdo não é copiado na sessão: o
  snapshot guarda só a `ContentVersion`, e quem restaura carrega o conteúdo publicado dessa versão.
  - Prós: os internos continuam internos; o formato gravado é um contrato explícito, testável com fixtures, que
    um refactor do domínio não muda sem querer; a validação de `Restore` trata o banco como fronteira (o tipo nasce
    válido ou falha alto); o conteúdo vive uma vez em `experience_versions`, o que reforça a INV-09 e encolhe o
    estado de cada sessão; a pista privada no banco passa a ser só o índice do conjunto sorteado.
  - Contras: um DTO e um mapeamento de ida e volta por experiência e por `SchemaVersion`; o snapshot é um tipo
    público que pode ser serializado para o cliente por engano, e precisa de teste de arquitetura que impeça isso;
    `RoutesControlState` muda e `GameSession.Start` passa a receber a versão da sala (ver Decisão).
- **(b) `InternalsVisibleTo("Duora.Infrastructure")` com `[JsonInclude]`/`[JsonConstructor]` nos tipos
  internos.** A Infrastructure serializa `UnlikelyRoutesState`, `GameSession` e o registro de ações direto.
  - Prós: nenhum DTO novo; menos código no primeiro marco.
  - Contras: o `InternalsVisibleTo` vale para o assembly inteiro, então a Infrastructure passa a enxergar todos
    os internos de todos os módulos do Core, e a fronteira que hoje o compilador garante vira disciplina; o
    formato no banco passa a ser o formato do domínio, e renomear um campo interno quebra sessões ativas em
    silêncio, só descoberto no deploy; atributos de serialização entram no domínio; `ImmutableDictionary` com
    chave `ParticipantId` e a hierarquia fechada de ações exigem conversores e `[JsonDerivedType]`; o conteúdo
    embutido em `RoutesControlState` seria copiado em cada sessão (até 6 paradas e 8 pistas por linha), duplicando
    o catálogo.
- **(c) Tabelas relacionais por experiência** (`routes_choices`, `routes_clue_assignments` etc.).
  - Prós: constraints e tipos do banco sobre cada campo; consultas analíticas por SQL.
  - Contras: uma migração por experiência e por versão de schema, com formatos antigos e novos convivendo pela
    INV-09; o estado é sempre carregado e gravado inteiro, então as tabelas não ganham nada em leitura; a spec
    10.1 já aceita JSONB para exatamente este caso; o custo de publicar uma experiência nova cresce sem
    benefício de negócio.
- **(d) Event sourcing: reconstruir a sessão reaplicando o registro de ações.** Descartada: exige que todas as
  versões de regras reapliquem de forma idêntica para sempre, e o sorteio inicial não é reproduzível entre
  versões do runtime (`SessionSeed` documenta que `System.Random` com semente não garante a mesma sequência). Não
  há requisito de auditoria ou histórico que pague esse custo (regra de DDD 2 e 16).

## Decisão

Vamos adotar a alternativa **(a)**.

1. **Snapshot no Core.** `Duora.Core.Experiences` ganha `GameSessionSnapshot`, um record público só de
   primitivos e DTOs: `experienceId`, `rulesVersion`, `contentVersion`, `schemaVersion`, os dois `participantId`,
   `startedAt`, `deadline`, `status`, `version`, o estado da experiência (`ExperienceStateSnapshot` com três
   partes: pública, controle e privada por participante) e o registro de ações. `GameSession` ganha
   `ToSnapshot()` e `static Restore(GameSessionSnapshot snapshot, TContent content)`. O estado e o registro
   continuam `internal`. Status, versão, início e prazo vêm de **colunas** da linha (`state`, `version`,
   `game_started_at`, `active_deadline_at`), não do JSONB: a linha tem uma fonte da verdade por fato, e o job de
   prazo e as consultas da sala leem colunas sem abrir o estado de jogo.
2. **Estratégia por experiência.** Cada experiência implementa a tradução do seu `TState` para o DTO da sua
   `SchemaVersion` e de volta, ao lado das regras (`IExperienceRules` ganha os dois métodos ou uma interface
   irmã, a decidir na implementação). Só o Core conhece o formato interno; a Infrastructure só serializa DTOs
   públicos com `System.Text.Json`, sem atributos no domínio.
3. **Conteúdo pela versão fixada.** O snapshot guarda `contentVersion`; o conteúdo vem de
   `experiences.experience_versions` pela FK `experience_version_id` (já `RESTRICT`). `RoutesControlState` deixa
   de embutir o conteúdo no formato persistido: `Restore` recebe o conteúdo e falha se a `ContentVersion` ou o
   `ExperienceId` não baterem com o snapshot. As escolhas são gravadas por `stepId`/`optionId` e a pista privada
   por índice do conjunto sorteado, nunca pelo texto.
4. **Colunas.** `game_sessions.public_state` e `control_state` (inclui o resultado do sorteio e os limites
   fixados); `game_participants.private_state` por pessoa; `game_sessions.schema_version int` igual à da versão
   publicada (`ExperienceVersion` usa `int` para as três versões, e `experience_versions` segue o mesmo tipo);
   `game_sessions.game_started_at timestamptz`. O estado de jogo existe se e só se o jogo começou, e o marco é
   `game_started_at`, não o `state`: uma sala pode ir de `Invited` ou `Ready` direto a `Expired` ou `Aborted` sem
   nunca ter jogo, e de `Active` a `Completed`, `Expired` ou `Aborted` com jogo. `CHECK`s: `(game_started_at IS
   NULL) = (public_state IS NULL)` e idem para `control_state`; `state <> 'Active' OR game_started_at IS NOT
   NULL`; `state NOT IN ('Invited', 'Ready') OR game_started_at IS NULL`. `game_participants.private_state` é
   gravado na mesma transação que preenche `game_started_at`, e o `Restore` falha se faltar o de alguém. `game_sessions.version bigint` é a **única**
   versão da sala (ADR-0016): nasce 1 no `Invited`, sobe em toda transição, inclusive as anteriores ao jogo, e é a
   `StateVersion` do motor. Por isso `GameSession.Start` passa a receber a versão corrente da sala em vez de
   partir sempre de `StateVersion.Initial`; `CHECK (version >= 1)` em vez do `default 0` da convenção geral. Cada
   coluna JSONB tem `CHECK (octet_length(col::text) <= 32768)`.
5. **Registro de ações sem payload.** `experiences.game_actions` guarda `actor_id`, `action_id`, `type`,
   `payload_sha256 bytea` (SHA-256 da forma canônica da ação, gerada pelo Core), `expected_version`,
   `applied_version` e `created_at`; a PK `(game_session_id, actor_id, action_id)` continua (spec 10.3).
   `RecordedAction` compara autor, versão esperada e hash, em vez de `Action.Equals`, para que a regra
   de reenvio da spec 8.7 funcione igual antes e depois de um reinício (já implementado com o
   `ActionFingerprint`; a forma canônica é contrato e muda só com rótulo novo, `duora.game-action/v2`, lido ao lado
   da v1 enquanto houver sessão ativa com ela). `Restore` carrega o registro inteiro da sessão. Hoje o tamanho do
   registro só é limitado implicitamente pelas regras (etapas e revisões finitas); cada experiência passa a
   declarar um teto explícito de ações aplicadas, que o motor aplica ao validar e o `Restore` confere.
6. **Validação na volta.** `Restore` trata o snapshot como entrada externa: confere par, versões, etapa,
   índices de pista, `stepId`/`optionId` contra o conteúdo, coerência entre `status` e etapa, `actionId` único na
   sessão (o motor indexa o registro só pelo id, enquanto a PK do banco inclui o autor) e o teto de ações. Snapshot inválido
   lança exceção de domínio específica; a API responde erro genérico e o alerta leva só `gameSessionId` e o tipo
   da falha.
7. **Concorrência (spec 10.4).** Invariante: **todo escritor de `game_participants` e `game_actions`
   (aceite de convite, saída, ação de jogo, prazo, exclusão de conta) tranca antes a linha de `game_sessions` da
   mesma sala**; nenhuma dessas tabelas é escrita sem esse lock, e é isso que torna a leitura das três tabelas
   sob o lock da sala um snapshot coerente. Todo comando que muda a sala segue a ordem global: 0 idempotência; guardas e
   coordenadores quando o caso de uso pedir (decisão, bloqueio); 4 `SELECT ... FROM experiences.game_sessions
   WHERE id = $id FOR UPDATE`, depois as linhas de `game_participants` e `game_actions` da sessão; `Restore`;
   motor; gravação com `UPDATE experiences.game_sessions SET ..., version = version + 1 WHERE id = $id AND version
   = $v`, `UPDATE` das linhas privadas alteradas, `INSERT` em `game_actions`, outbox e resultado de idempotência
   no mesmo commit. Zero linhas no `UPDATE` condicional é bug do protocolo (o lock deveria impedir) e vira
   rollback com conflito, nunca sobrescrita. Leitura de projeção não tranca, mas lê `game_sessions`,
   `game_participants` e `game_actions` numa transação `REPEATABLE READ READ ONLY` (ou num único comando): em
   `READ COMMITTED` cada comando vê um snapshot novo, e uma ação comitada entre as leituras daria um snapshot com
   estado público de uma versão e privado de outra, que o `Restore` recusaria ou, pior, aceitaria incoerente. O
   job de prazo é o do [ADR-0021](0021-jobs-duraveis-e-leases-no-postgresql.md) (`active-deadline:<sessão>:<prazo>`,
   criado na transação que leva a sala a `Active`, e um novo a cada mudança de prazo) e segue a C6 com a mesma gravação condicional.
8. **Evolução de `SchemaVersion` (expand/contract e tolerant reader).** Uma sessão grava sempre na
   `schemaVersion` com que nasceu (INV-09); só uma versão publicada nova da experiência leva sessões novas a um
   schema novo. Expand: a release N passa a ler os schemas `k` e `k + 1` e as regras das duas `rulesVersion`;
   sessões antigas continuam em `k`. Contract: o leitor e as regras de `k` só saem quando nenhuma linha dentro do
   prazo de retenção usa `k` (consulta `count(*) WHERE schema_version = k` no PR que remove). O leitor ignora
   campos desconhecidos e preenche ausentes com o default documentado no DTO; mudança que não cabe nisso exige
   `SchemaVersion` nova. Fixtures JSON de cada schema publicado ficam versionadas e são lidas por teste enquanto o
   leitor existir.
9. **O que fica privado no banco (INV-05).** `private_state` (índice das pistas de cada pessoa), `control_state`
   (resultado do sorteio, quem escolhe primeiro) e `seed` são classe P3 e nunca vão a API, log, métrica, issue ou
   contexto de agente. O banco não filtra por destinatário: a garantia é a projeção do Core
   (`ProjectForParticipant`) e a regra de que nenhum endpoint devolve snapshot. O papel `duora_app` lê tudo, então
   o vazamento se previne no código e se detecta pelos testes da Compliance.

Porquê técnico: mantém a fronteira que o compilador já garante, separa o formato persistido do formato interno e
dá à evolução de schema um contrato explícito e testável. Porquê de negócio: experiência nova ou revisão de
conteúdo não exige migração de tabela, e sessões em andamento atravessam deploys sem perder estado.

## Consequências

- Positivas: domínio sem atributo de persistência nem `InternalsVisibleTo` novo; conteúdo guardado uma vez por
  versão; menos dado sensível por linha (índices em vez de textos de pista); reenvio idempotente funciona depois
  de reinício; o formato antigo é coberto por fixtures.
- Negativas: cada experiência paga um DTO e um mapeamento de ida e volta por `SchemaVersion`, com testes;
  `RoutesControlState` e `GameSession.Start` mudam antes da primeira migração; restaurar uma sessão exige ler o
  conteúdo da versão (cacheável em memória por ser imutável); sem o payload das ações não dá para reconstruir a
  partida lance a lance, só o estado atual.
- Trade-off: aceito o custo de mapeamento para não acoplar o banco ao formato interno. Se surgir requisito de
  histórico lance a lance (moderação, disputa), o payload volta a `game_actions` por ADR, com limite e classe P3.
- Follow-up de documentação (feito no PR de aceite): [model.md](../../data/model.md#schema-experiences)
  (`payload` → `payload_sha256`, `schema_version int`, `game_started_at`, `CHECK (version >= 1)`, estado anulável
  antes de `Active`, prazos pelo `ops.jobs`) e o C4 de [concurrency.md](../../data/concurrency.md#c4-duas-ações-de-jogo).
  Pendente para a implementação: `Restore`/`ToSnapshot`, `Start` com a versão da sala, teto de ações, migração e
  os testes da Compliance.

## Segurança e operação

- O snapshot é público no assembly, mas não é contrato HTTP: nenhum tipo de `Duora.Contracts` nem retorno de
  endpoint pode referenciá-lo.
- JSONB de jogo e `seed` ficam fora de log estruturado, de telemetria e de mensagens de exceção; a exceção de
  snapshot inválido carrega só o ID da sessão e o código da falha.
- Exportação do titular (Privacy) inclui o próprio `private_state` e o estado público, nunca o `private_state`
  do outro participante.
- Backup e restore não mudam: o estado está no PostgreSQL, coberto pelo pgBackRest
  ([ADR-0011](0011-pgbackrest-wal-externo-restore-obrigatorio.md)).
- Métrica: tamanho do snapshot por experiência (percentil 99) para antecipar o teto de 32 KiB.
- `game_sessions` é atualizada a cada ação com JSONB de até 32 KiB (TOAST): poucas ações por sala e no máximo 50
  salas simultâneas por evento (spec 4.3) não justificam ajuste prévio; o ensaio de carga confere `n_dead_tup` e
  o tamanho do TOAST, e autovacuum por tabela só entra com essa medida.

## Compliance

- **Ida e volta (unidade, M2):** para N sequências de ações válidas geradas com `Random` de semente fixa (por
  `[Theory]`), `Restore(ToSnapshot(s), content)` tem a mesma versão, status e prazo de `s`, a mesma projeção
  para cada participante e devolve `AlreadyApplied` ao reenviar uma ação já aplicada. Valor esperado da projeção
  conferido também contra literais fixos em casos escritos à mão.
- **Fixtures de schema (unidade, M2):** `Restore` lê os JSON versionados de cada `schemaVersion` publicado; o
  teste quebra se um refactor mudar o formato lido. Fixtures inválidos (pista fora do conjunto, `optionId`
  inexistente, `status` incoerente) lançam a exceção de snapshot inválido.
- **Reinício (aceite da 6.2, integração, M2):** `RestartDuringCluesStagePreservesStateAndDeadline`. Com
  Testcontainers, uma `WebApplicationFactory` cria a sala e leva o par à etapa de pistas; o teste guarda as
  projeções de A e B e o `active_deadline_at`; descarta a factory e sobe outra contra o mesmo banco; as
  projeções e o prazo são iguais; avançar o `FakeTimeProvider` além do prazo faz o job de expiração resolver a
  sala uma vez só.
- **T-09 (integração, M2):** com a versão 1 publicada e uma sessão ativa, publicar a versão 2 com textos e
  paradas diferentes; aplicar uma ação e projetar a sessão antiga mostra o conteúdo da versão 1; uma sessão nova
  usa a versão 2; tentar alterar o conteúdo publicado da versão 1 falha.
- **T-05 (HTTP, M2):** o JSON da projeção de A não contém nenhuma pista de B nem o resultado do sorteio, inclusive
  em erro e notificação; canário semeado no texto da pista de B nunca aparece em resposta nem em log.
- **Leitura consistente (integração, M2):** com um gancho de teste entre os comandos da leitura, uma ação de B
  comitada no meio de uma projeção de A não produz snapshot misto: a projeção é devolvida com sucesso e traz a
  versão `v` e o estado público e privado dessa mesma versão (a de antes da ação de B); uma segunda leitura
  traz `v + 1`. Sabotagem com `READ COMMITTED` precisa falhar (versões divergentes ou `Restore` recusando).
- **Estado só com jogo (integração, M2):** sala `Invited` e `Ready` vão a `Expired` e `Aborted` sem estado de
  jogo; sala `Active` não grava sem `game_started_at` e estado; os `CHECK`s recusam as combinações inválidas.
- **Lock da sala (arquitetura ou revisão, M2):** todo SQL que escreve em `game_participants` ou `game_actions`
  fica no repositório da sessão, atrás do método que tranca `game_sessions` primeiro; o teste de integração do
  C4 confere com `pg_locks` que a linha da sala está trancada no momento da escrita.
- **Versão única (unidade e integração, M2):** sala que passou por `Invited` e `Ready` começa o jogo com a versão
  corrente da linha, e o primeiro `expectedVersion` aceito é essa versão.
- **Arquitetura (M2):** `Duora.ArchitectureTests` falha se `Duora.Contracts` ou `Duora.Web` expuser
  `GameSessionSnapshot`, `ExperienceStateSnapshot` ou um DTO de estado de experiência em tipo de resposta, e se o
  Core ganhar `InternalsVisibleTo` para a Infrastructure. Reforço barato no contrato: nenhum schema de
  `docs/api/openapi.json` tem nome terminado em `Snapshot` nem as propriedades `controlState`/`privateState`.
- **Concorrência (integração, M2):** C4 e C6 seguem a técnica de [concurrency.md](../../data/concurrency.md#técnica),
  inclusive a sabotagem que remove o `WHERE version = $v`.
