# ADR-0004: Um `DuoraDbContext` inicial com schemas lógicos por área

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 3.3, 10.1–10.5 e 23.1 (ADR-004)
- **Revisar quando:** as fronteiras amadurecerem e houver estratégia comprovada de atomicidade entre contextos

## Contexto

Transações críticas atravessam conta, relação, decisão e outbox (decisão mútua, bloqueio, cancelamento de
evento). Um contexto EF por módulo obrigaria a coordenar transações entre contextos sem ganho no piloto. Ao
mesmo tempo, um contexto único convida a acesso livre a qualquer tabela.

Alternativas consideradas:

- **Um `DbContext` por módulo.** Adiado: só entra com estratégia comprovada de atomicidade e coordenação.
- **Schema único sem dono.** Descartado: apaga a propriedade lógica das tabelas.
- **`GenericRepository<T>` sobre toda a API do EF Core.** Descartado: expõe tudo a todos (spec 3.3).

## Decisão

Vamos usar um `DuoraDbContext` com schemas lógicos `identity`, `profiles`, `events`, `experiences`, `social`,
`safety`, `media`, `ops` e `privacy`, nomes físicos em `snake_case` por mapeamento explícito, UUID para
identidades, `timestamptz` para instantes UTC e `version bigint` gerenciada pela aplicação como concurrency
token. Cada tabela tem um módulo dono ([modules.md](../modules.md)); consultas e repositórios específicos ficam
na infraestrutura do módulo, e os casos de uso dependem de contratos estreitos.

Porquê técnico: atomicidade local nas transações que protegem INV-02, INV-03, INV-04 e INV-06. Porquê de
negócio: menos mecanismo de coordenação para construir e operar.

## Consequências

- Positivas: transação única com outbox; constraints e índices parciais protegem as invariantes no banco.
- Negativas: o compilador não impede um módulo de ler tabela alheia pelo contexto compartilhado; a fronteira
  depende de revisão e de testes de arquitetura.
- Trade-off: dividir o contexto depois exige migração de código, não de dados, desde que a propriedade das
  tabelas tenha sido respeitada.

## Segurança e operação

Conta de aplicação sem superuser nem DDL; migrador com credencial separada; migrações por job exclusivo, nunca
`Database.Migrate()` no startup (spec 10.7, 17.4). `DbContext` é scoped e nunca compartilhado entre tarefas
paralelas (spec 10.5).

## Compliance

- Teste de integração aplica as migrações num PostgreSQL 18 real (existente desde M0).
- Teste de arquitetura de acesso a tabelas por módulo: a implementar quando os módulos tiverem repositórios
  (M1).
- Revisão de migração confere schema, dono da tabela e SQL gerado antes de produção (spec 14.2).
