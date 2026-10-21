# Datas e fusos de eventos — revisão da issue #81

Base inspecionada: `259e5a2d0523548cc223c5f179192df778d56ff0`. Implementação para revisão humana; não encerra toda a issue #81.

## Escopo e contrato

Spec 13.6, ADR-0026 proposta 4, API-E01 (`GET /events`) e publicação administrativa de eventos. INV-01/05/12 preservadas: sem mudança de elegibilidade, autorização ou acesso a dados. Não há nova telemetria, locks ou transições de estado. Não há migração de preferência de conta, cobrança, telefone ou alteração de e-mails.

A inspeção encontrou `time_zone` no modelo documentado, mas não no SQL de `ParticipationSchema`. A migração aditiva cria `events.events.time_zone text null`. O campo opcional `timeZone` na publicação aceita IDs IANA da lista do runtime .NET e `UTC`; valores vazios, desconhecidos ou nomes Windows são recusados com 400. Omitir o campo mantém compatibilidade com publicadores existentes e significa **desconhecido**, não UTC nem o fuso do administrador. `GET /events` devolve o campo nullable. Instantes e agendamento não são recalculados ao guardar o fuso.

Registros históricos permanecem nulos: um instante não permite recuperar o fuso original. A interface identifica essa ausência e mostra referência UTC, além do horário do navegador quando diferente. Aplicar a migração antes do novo backend; o frontend tolera resposta antiga sem o campo. Para rollback, reverter os binários e preservar a coluna, sem apagar dados. Nenhuma migração foi aplicada fora do PostgreSQL descartável dos testes.

## Usos revisados

- `EventsPage`: horário no fuso do evento; horário completo no fuso do navegador quando os IDs resolvidos diferem. Não comparar apenas offsets, pois dois fusos podem divergir em outra estação. O elemento `time` mantém o instante original no atributo `datetime`.
- `SessionList`: instantes de início/último uso no fuso do navegador, agora rotulado.
- `RoomPage`: prazo do convite no fuso do navegador, agora rotulado. A sala não fornece um fuso de evento; não inferir um a partir do idioma ou da hora.
- `I18nProvider`: constrói os formatadores ao trocar idioma. Fuso do navegador é contexto de apresentação, **não uma preferência salva da conta**.
- Datas civis não passam por esses formatadores de instantes. A fronteira de eventos rejeita `YYYY-MM-DD` e data/hora sem offset, evitando que `new Date` converta silenciosamente uma data civil. Não se criou um formatador civil sem consumidor.
- Avisos de segurança continuam em UTC, sem inferência de preferência individual.

## Comparação real de runtime

Reproduzir a partir de `frontend/`: `node scripts/compare-time-zones.mjs` (browsers instalados do Playwright). Instante sintético `2026-10-16T23:00:00Z`.

Node 24.21.0 / ICU 78.3 e WebKit 27.2:

| Idioma | Fuso | `short` | `shortGeneric` |
|---|---|---|---|
| pt-BR | America/New_York | 19:00 GMT-4 | 19:00 Horário Nova York |
| pt-BR | America/Sao_Paulo | 20:00 BRT | 20:00 BRT |
| pt-BR | UTC | 23:00 UTC | 23:00 GMT+0 |
| en-US | America/New_York | 7:00 PM EDT | 7:00 PM ET |
| en-US | America/Sao_Paulo | 8:00 PM GMT-3 | 8:00 PM São Paulo Time |
| en-US | UTC | 11:00 PM UTC | 11:00 PM GMT+0 |

Chromium 156.0.8078.4 coincidiu em `short`, mas em `shortGeneric` apresentou `19:00 Horário New York` e `8:00 PM Sao Paulo Time`. A tradução geográfica depende dos dados ICU/CLDR do navegador; não prometer uma grafia única entre runtimes.

**Implementado:** `short`, conforme a proposta existente, com campos explícitos de data/hora. `dateStyle`/`timeStyle` não aceitam `timeZoneName` na mesma configuração. Os campos mantêm os formatos curtos anteriores de pt-BR/en-US.

**Recomendação de experiência, pendente de aprovação:** usar identificação geográfica (`shortGeneric`) para facilitar a leitura internacional, acompanhada de offset quando necessário para desambiguar a hora repetida; manter UTC rotulado como UTC. Não adotar uma regra de “fuso da casa” inferido pelo idioma, porque idioma não determina localização. O formato exato e o espaço ocupado devem ser aprovados antes de alterar os exemplos do ADR/design. Nesta correção esses exemplos continuam coerentes com `short`.

Na volta do horário de verão, `2026-11-01T05:30Z` e `2026-11-01T06:30Z` são ambos 01:30 em Nova York. `short` distingue GMT-4/GMT-5 em pt-BR e EDT/EST em en-US; `shortGeneric` sozinho os rotula igualmente. Também permanece pendente o produto de preferência IANA na conta e seu uso em e-mails; esta entrega não declara esse critério da issue concluído.

## Verificação

- Literais em `translator.test.ts`: os três fusos nos dois idiomas; antes/depois da transição de março; os dois instantes de 01:30 em novembro; virada de data; fusos iguais e distintos; legado desconhecido. Expectativas não são recalculadas com Intl.
- `participation-api.test.ts`: data civil e horário sem offset recusados; instante com offset e ID IANA preservados.
- `EventTimeZoneTests`: publicação HTTP, validação negativa, leitura no PostgreSQL real migrado, transporte pela API e legado nulo.
- `event-time-zones.spec.ts`: resposta API → tela real → textos literais e atributo `datetime`, nos dois idiomas e três projetos de navegador.
- Testes de sessões atualizados para exigir UTC rotulado.

Os resultados executados e os gates ainda pendentes são registrados no relatório de entrega/PR. Strings localizadas podem mudar após atualização de runtime; a revisão deve comparar saídas reais antes de atualizar os literais.
