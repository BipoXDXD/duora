# Datas e fusos de eventos — revisão da issue #81

Registro histórico da primeira entrega (a conclusão do escopo atual consta no complemento final abaixo). Base daquele complemento: `d79321d`. As três escolhas de UX foram aprovadas em 2026-10-21 (adendo do ADR-0026). PR para revisão humana; sem merge automático e sem encerrar toda a issue #81.

## Escopo e contrato

Spec 13.6, ADR-0026 proposta 4, API-E01 (`GET /events`) e publicação administrativa de eventos. INV-01/05/12 preservadas: sem mudança de elegibilidade, autorização ou acesso a dados. Não há nova telemetria nem mudança de transições de participação. O complemento acrescenta preferência privada da conta e sua apresentação nos avisos de credenciais. Cobrança e telefone ficam fora deste recorte.

A inspeção encontrou `time_zone` no modelo documentado, mas não no SQL de `ParticipationSchema`. A migração aditiva cria `events.events.time_zone text null`. O campo opcional `timeZone` na publicação aceita IDs IANA da lista do runtime .NET e `UTC`; valores vazios, desconhecidos ou nomes Windows são recusados com 400. Omitir o campo mantém compatibilidade com publicadores existentes e significa **desconhecido**, não UTC nem o fuso do administrador. `GET /events` devolve o campo nullable. Instantes e agendamento não são recalculados ao guardar o fuso.

Registros históricos permanecem nulos: um instante não permite recuperar o fuso original. A interface identifica essa ausência e mostra referência UTC, além do horário do navegador quando diferente. Aplicar a migração antes do novo backend; o frontend tolera resposta antiga sem o campo. Para rollback, reverter os binários e preservar a coluna, sem apagar dados. Nenhuma migração foi aplicada fora do PostgreSQL descartável dos testes.

## Usos revisados

- `EventsPage`: horário no fuso do evento; horário completo no fuso salvo (ou navegador quando não há escolha) quando os IDs resolvidos diferem. Não comparar apenas offsets, pois dois fusos podem divergir em outra estação. O elemento `time` mantém o instante original no atributo `datetime`.
- `SessionList`: instantes de início/último uso no fuso salvo ou, quando ausente, no navegador, explicitamente rotulado.
- `RoomPage`: prazo do convite no fuso salvo ou, quando ausente, no navegador, explicitamente rotulado. A sala não fornece um fuso de evento; não inferir um a partir do idioma ou da hora.
- `I18nProvider`: constrói os formatadores ao trocar idioma. `AccountTimeZoneProvider` deriva da identidade autenticada; sair ou trocar de conta remove a preferência anterior. Não existe preferência de fuso em localStorage.
- Datas civis não passam por esses formatadores de instantes. A fronteira de eventos rejeita `YYYY-MM-DD` e data/hora sem offset, evitando que `new Date` converta silenciosamente uma data civil. Não se criou um formatador civil sem consumidor.
- Avisos de segurança mantêm suas frases bilíngues UTC e acrescentam horário local quando há escolha explícita, capturada na outbox. Nada é inferido de idioma/IP/residência.

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

Chromium 156.0.8078.4 coincidiu em `short`, mas em `shortGeneric` apresentou `19:00 Horário New York` e `8:00 PM Sao Paulo Time`. No CI Linux (run 39564730497), WebKit também apresentou `Sao Paulo Time`, sem acento. Os testes E2E selecionam o literal pela plataforma e pelo navegador, mantendo igualdade do texto completo, datas, offsets e `datetime`; não calculam a expectativa com Intl. A tradução geográfica depende dos dados ICU/CLDR do navegador; não prometer uma grafia única entre runtimes.

**Implementado após aprovação:** eventos usam `shortGeneric` + offset UTC do instante, em duas linhas quando os fusos diferem. UTC é mostrado uma só vez; fallback de nome para offset não duplica o deslocamento. Outros instantes continuam com `short` e respeitam a escolha salva quando existe. `dateStyle`/`timeStyle` não podem acompanhar `timeZoneName`; campos explícitos preservam o formato curto anterior.

Na volta do horário de verão, `2026-11-01T05:30Z` e `2026-11-01T06:30Z` são ambos 01:30 em Nova York. O nome genérico fica igual, mas `UTC−04:00` e `UTC−05:00` desambiguam. A transição de março pula de 01:59 para 03:00. São Paulo mantém UTC−03:00 nesses quatro instantes.

## Preferência privada e concorrência

`GET/PUT /me/time-zone` devolve `{timeZone: string|null, version: string}` e ETag; PUT exige If-Match. `timeZone` é obrigatório no corpo, aceita null explícito para remover e recusa vazio, ID Windows ou desconhecido com 400. Campo omitido não apaga a preferência. `GET /me/time-zone/options` oferece a lista IANA suportada pelo servidor mais UTC, autenticada. `/me` projeta `preferredTimeZone` só para o titular.

A migração `20261021090001_AccountTimeZone` acrescenta `identity.users.preferred_time_zone` e `time_zone_version`, com default null/0 para contas novas e legadas. A gravação trava a linha da conta, revalida status Active/PendingEligibility e a versão. Uma disputa da mesma versão tem um vencedor; a outra recebe 412. Falta de If-Match: 428. A espera de lock é limitada e retorna 503. Sem alteração de guardas de Matching, stamps ou piso de login de 350 ms. O Identity não grava estes dois campos em updates da entidade: uma instância antiga não pode apagar a escolha recente.

O formulário não faz autosave. O botão do dispositivo preenche o rascunho; Save confirma; vazio + Save remove. Em 412, mantém rascunho, desabilita Save até recarregar a versão e exibe o valor salvo atual para comparação. Mudança de idioma/dispositivo não modifica a escolha. Resposta tardia de outra identidade não altera a conta atual.

## Implantação e rollback (sem executar neste PR)

1. Aplicar a migração aditiva antes do novo web; preservar colunas e valores em rollback de binários. O Down recusa apagar preferências.
2. Implantar primeiro o **worker leitor** de `preferredTimeZone`; só depois habilitar o novo web/SPA que grava escolhas e avisos com esse campo. Payload sem preferência omite o campo, preservando a forma antiga.
3. Aviso captura o fuso na transação da mudança de credencial; remover/mudar depois não reescreve o aviso. Worker valida IANA e recusa campo em outros tipos ou aviso com token. Mantém referência UTC, bilinguismo, deadlines, leases e retry existentes.
4. Em rollback, manter o novo worker enquanto existirem entregas Pending/InFlight com `payload->>'preferredTimeZone' IS NOT NULL`. O leitor antigo recusa campos desconhecidos: **não** o restaurar antes da drenagem, nem apagar o campo para forçar compatibilidade. Reverter SPA/web não apaga preferências; clientes antigos ignoram o novo campo de `/me`.

## Verificação

- Literais em `translator.test.ts`: os três fusos nos dois idiomas; antes/depois da transição de março; os dois instantes de 01:30 em novembro; virada de data; fusos iguais e distintos; legado desconhecido. Expectativas não são recalculadas com Intl.
- `participation-api.test.ts`: data civil e horário sem offset recusados; instante com offset e ID IANA preservados.
- `EventTimeZoneTests`: publicação HTTP, validação negativa, leitura no PostgreSQL real migrado, transporte pela API e legado nulo.
- `event-time-zones.spec.ts`: resposta API → tela real → textos literais e atributo `datetime`, nos dois idiomas e três projetos de navegador.
- `AccountTimeZoneTests`: defaults, save/remove, IDs inválidos/omitidos, ETag, concorrência, isolamento e update Identity antigo. Inventário CSRF e política de elegibilidade incluem as novas rotas sem remover verificações.
- `AccountTimeZoneForm`/provider: escolha explícita, botão sem gravação, remoção, conflito, troca de conta e locale, fallback para ICU incompatível.
- Avisos: literais pt-BR/en-US para NY/SP/UTC, março/novembro, payload antigo, snapshot preservado após remoção e recusa de dados inválidos.
- E2E também cobre preferência distinta do navegador e ambas as ocorrências repetidas com instantes intactos.

Os resultados executados e os gates ainda pendentes são registrados no relatório de entrega/PR. Strings localizadas podem mudar após atualização de runtime; a revisão deve comparar saídas reais antes de atualizar os literais.


## Complemento final da #81 — quantidades

Os catálogos agora usam argumentos ICU `number` para quantidades (revisões restantes e limites de caracteres), passando pelo `Intl.NumberFormat` usado por `intl-messageformat`. Plurais já usam esse formatador para `#`. Códigos, versões legais, identificadores e horários previamente formatados não são tratados como quantidades. A troca de idioma recria o tradutor e escolhe os separadores correspondentes; os valores de domínio e limites não mudam.

`translator.test.ts` compara os literais `1.234` em pt-BR e `1,234` em en-US para dois consumidores, sem calcular a expectativa com Intl. Os testes de datas/fusos listados acima permanecem intactos, inclusive DST, datas civis recusadas como instantes e preferência IANA validada pelo servidor.

O escopo atual da #81 fica coberto pelas entregas de fusos já integradas e por este complemento. Cobrança e telefone não existem no produto atual: quando forem introduzidos, continuam obrigatórios valor em unidade mínima (`bigint`) com moeda ISO 4217 explícita na mesma linha, sem BRL implícito, e telefone E.164. Não foram criados módulos ou formatadores sem consumidor para simular a entrega desses recursos futuros. Nenhuma mudança de API, persistência, autenticação ou política de pareamento.
