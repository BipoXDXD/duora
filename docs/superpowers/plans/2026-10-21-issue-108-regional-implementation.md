# Plano de implementação regional do Duora — issue 108

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task after explicit authorization. Steps use checkbox (`- [ ]`) syntax for tracking. This delivery is planning only.

**Goal:** implementar futuramente declaração/preferência regional por país, filtro bilateral e sugestão opcional por IP, conforme ADR-0033, sem ativar países adicionais.

**Architecture:** Profiles possui a declaração e a preferência privada; Matching consome uma projeção mínima e revalida sob guardas. A detecção é uma sugestão temporária independente. Um atualizador separado prepara artefatos imutáveis; o processo web somente lê versões validadas e admissíveis.

**Tech Stack:** .NET 10, ASP.NET Core, Npgsql/EF Core, PostgreSQL 18, React/TypeScript, Vitest/Playwright; IPinfo IP to Country `country.mmdb`, CC BY-SA 4.0. Leitor MMDB e licença de software ainda exigem validação antes da dependência.

**Spec:** [ADR-0033, incluindo os dois adendos de 21/10/2026](https://github.com/BipoXDXD/duora/blob/7f1cea3197e283b84f32dc1e3e0d30efa3c69431/docs/architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md); [issue #108](https://github.com/BipoXDXD/duora/issues/108); ADR-0030 para perfil/catálogo. Base técnica inspecionada: `7f1cea3197e283b84f32dc1e3e0d30efa3c69431`.

**Status:** plano aprovado pelo responsável em 21/10/2026; publicação documental autorizada; execução da implementação ainda não autorizada. Nenhuma etapa de implementação executada. Não abre PR de produto, aplica migração, baixa base, instala dependência ou ativa operação. Responsável nominal titular, substituto e canal de alertas permanecem pendentes.

## Restrições globais

- Catálogo executável BR + 27 UFs; nenhum seed US. US em testes somente como fixture sintética isolada, nunca migração de produção.
- País é granularidade do filtro; subdivisão opcional não restringe pares. Não introduzir cidade, coordenadas, CEP ou distância.
- `pending` impede novos pares; `countries` exige conjunto não vazio; `unrestricted` exige escolha explícita. Localização continua opcional.
- País ausente não satisfaz restrição de país específico. Aceitação bilateral é sobre o país declarado da outra pessoa, não interseção entre preferências.
- Escolha manual prevalece; detecção não grava nem altera idioma, locale, fuso, declaração ou preferência. Espera não relaxa filtros.
- Uma reserva por participante; manter elegibilidade, bloqueios, histórico e acordo linguístico V2 em todas as novas regras.
- ETag/If-Match, autorização do titular, CSRF e no-store; dados de preferência não aparecem no perfil de terceiros.
- Nenhum I/O externo sob locks de pareamento; nenhuma consulta por IP a API do fornecedor.
- Atualização 06h America/Sao_Paulo; aviso sem execução há 36h; aviso de idade a partir de 72h; suspensão da detecção a partir de 7 dias.
- Retenção: até 14 versões/14 dias desde download, registros sanitizados por 90 dias; recuperar apenas artefato validado com idade de publicação/construção inferior a 7 dias.
- Sem IP bruto/hash de participante, país por pessoa, token, URL assinada ou payload de consultas em logs/telemetria. Sem histórico de detecção.
- Aprovação deste plano, implementação, integração, aplicação de migrações e ativação são atos distintos. Gates existentes e revisão humana permanecem.

## Foco de revisão

1. Resposta de detecção atrasada após digitação manual: não altera qualquer rascunho editado (etapa 6).
2. Cliente legado muda `regionCode` durante reserva: participa da mesma guarda e não atravessa revalidação (etapa 3).
3. Perfil e preferência são recursos independentes e um dos salvamentos falha: UI não afirma sucesso conjunto (etapa 6).
4. Atualizador perde lease, retorna atrasado e tenta promover: só a geração vigente pode ativar a versão (etapa 8).
5. Relógio/data inválida, redownload e backup antigo: não rejuvenescem nem reativam base expirada (etapas 7–9).

## Pré-requisitos e pontos de parada

| Marco | Exigência | Bloqueia |
|---|---|---|
| G0 | Plano aprovado em 21/10/2026; autorização explícita para executar ainda necessária | Qualquer implementação |
| G1 | Ambiente isolado, PostgreSQL descartável, baseline dos testes e branch atualizada | PRs funcionais |
| G2 | Leitor MMDB/versão/licença aprovados, acesso autorizado a country.mmdb, schema/data/cadência e limites de download homologados | Dependência real, download e integração IPinfo |
| G3 | Titular, substituto e canal de alertas definidos; cobertura de atendimento e serviço/orçamento de armazenamento detalhados | Ativação da atualização/detecção em ambiente operacional |
| G4 | Migração ensaiada, versão nova dos consumidores, testes de concorrência, comunicação/configuração dos usuários existentes | Ativação do filtro regional |
| G5 | Primeiro snapshot homologado, atribuição, ensaios de recuperação/alertas e autorização de implantação | Ativação do detector |

G3 permanece aberto por instrução do responsável. Pode-se desenvolver e testar com fixtures sintéticas após G0/G1 sem inventar nomes ou canal. Não declarar operação pronta com esse marco aberto. BR pode receber o fluxo regional sem US; a habilitação internacional continua na #78.

A governança aprovada não especifica números para tamanho máximo do artefato, limite de requisições do endpoint ou cobertura de atendimento. O plano exige registrar esses valores na revisão do respectivo PR, com evidência de tamanho/carga e política operacional; não os trata como já aprovados.

## Sequência e divisão de entregas

| Entrega | Etapas | Resultado revisável |
|---|---|---|
| A | 1–2 | Domínio/persistência/API de preferência manual, sem ativação do filtro |
| B | 3–4 | Protocolo de concorrência e seletor regional versionado, preparado para ativação controlada |
| C | 5–6 | Endpoint com adaptador sintético/desligado e interface manual/sugestão opcional |
| D | 7 | Leitura local de MMDB após homologação, desabilitada por padrão |
| E | 8–9 | Atualização, retenção, recuperação e alertas, sem ativação enquanto G3 estiver aberto |
| F | 10 | Homologação integrada e proposta de implantação/reversão |

Dependências: 1 → 2 → 3 → 4; 2/5 → 6; G2 → 7; 7 → 8 → 9; todas → 10. Cada PR deve continuar utilizável e manter novos comportamentos operacionais desligados até autorização. Não enviar frontend que dependa de servidor antigo incompatível.

## Etapa 1 — Modelo da preferência regional

**Arquivos existentes a reutilizar:** `backend/src/Duora.Core/Profiles/ProfileLocation.cs`, `backend/src/Duora.Core/Matching/Preferences.cs`, `backend/src/Duora.Infrastructure/Persistence/DuoraDbContext.cs`, `backend/src/Duora.Infrastructure/Profiles/PreferenceRecord.cs` como padrão.

**Criar:** `backend/src/Duora.Core/Profiles/RegionalPreference.cs`, `backend/src/Duora.Infrastructure/Profiles/RegionalPreferenceRecord.cs`, migração aditiva em `backend/src/Duora.Infrastructure/Persistence/Migrations/`, `backend/tests/Duora.UnitTests/Profiles/RegionalPreferenceTests.cs`, `backend/tests/Duora.IntegrationTests/Profiles/RegionalPreferenceMigrationTests.cs`.

**Modelo planejado:** `profiles.regional_preferences(user_id, mode, version, updated_at)` e `profiles.regional_preference_countries(user_id, country_code)`; PK composta na filha, FK para a preferência e catálogo existente. Versão própria. Ausência de linha representa `pending`, versão 0, sem backfill de consentimento.

```text
GET sem linha -> { mode: pending, countryCodes: [], version: "0" }
PUT { mode: countries, countryCodes: [BR] } -> válido
PUT { mode: countries, countryCodes: [] } -> 400
PUT { mode: unrestricted, countryCodes: [] } -> válido
PUT { mode: unrestricted, countryCodes: [BR] } -> 400
```

- [ ] Escrever primeiro os testes da matriz acima, duplicações, país não habilitado e null.
- [ ] Rodar testes e confirmar falha por comportamento ausente; implementar enum/validação e modelo mínimo.
- [ ] Garantir modo/lista consistentes no commit: constraints de enum/versão/FKs e constraint trigger diferida para consistência entre pai/filhas, com testes PostgreSQL reais.
- [ ] Migração não muda perfil, versões legadas, timestamps, idiomas, catálogo ou preferências linguísticas. Ausência vira pending por leitura, não por inferência.
- [ ] Atualizar inventário de tabelas/privilégios em `backend/tests/Duora.IntegrationTests/Database/AppRoleTableAccess.cs` e `infra/images/postgres/reconcile-privileges.sql`; aplicação lê catálogo, não o administra.
- [ ] Rodar integração com FKs inválidas, escrita direta inconsistente e migração sobre todos os estados legados. Commit somente após aprovação.

**Aceite:** estrutura aditiva preserva dados e torna impossível confirmar preferência inválida; nenhum participante recebe opt-in automaticamente.

## Etapa 2 — API de preferência regional independente

**Criar:** `backend/src/Duora.Contracts/Profiles/RegionalPreferenceContracts.cs`, `backend/src/Duora.Infrastructure/Profiles/RegionalPreferenceStore.cs`, `backend/src/Duora.Web/Profiles/RegionalPreferenceEndpoints.cs`, `backend/tests/Duora.IntegrationTests/Profiles/RegionalPreferenceTests.cs`.

**Modificar:** registro das rotas/serviços na Web, `docs/api/README.md`; regenerar `docs/api/openapi.json` e `frontend/src/shared/api/schema.d.ts`.

**Interfaces planejadas:** `GetOwnAsync(Guid userId, CancellationToken)` retorna mode/countryCodes/version; `UpdateOwnAsync(Guid userId, long expectedVersion, RegionalPreference value, CancellationToken)` retorna estado salvo ou conflito. `GET/PUT /api/v1/me/regional-preferences`; PUT recebe `{mode,countryCodes}` e cabeçalho If-Match. Sem parâmetro de usuário externo.

- [ ] Testar GET pending sem criar linha e sem afetar a declaração linguística.
- [ ] Testar PUT com ETag ausente 428, versão antiga 412, corpo inválido 400; manter semântica de ETags inválidos do projeto.
- [ ] Implementar gravação atômica de pai/filhas sob guarda do usuário, revalidando conta e versão; incrementar versão da preferência e marcador de segurança existente.
- [ ] GET usa uma consulta coerente para modo/lista/versão, evitando leituras de versões diferentes entre comandos.
- [ ] Testar autorização, CSRF, no-store, conta restrita, duplicatas e duas gravações com mesma versão: uma vence, outra recebe 412, sem filhos parciais.
- [ ] Atualizar cliente/contrato gerados e executar gate de compatibilidade. Commit independente.

**Aceite:** editar região não altera ETag/dados de idiomas; API antiga de idiomas não apaga preferência regional.

## Etapa 3 — Serializar localização declarada com pareamento

**Modificar:** `backend/src/Duora.Infrastructure/Profiles/ProfileStore.cs`; reutilizar o protocolo de `backend/src/Duora.Infrastructure/Identity/LanguagePreferenceGuard.cs` e `backend/src/Duora.Infrastructure/Participation/ParticipationSql.cs`.

**Testar:** `backend/tests/Duora.IntegrationTests/Profiles/RegionalPreferenceGuardTests.cs` (novo), testes existentes de perfil/localização e `backend/tests/Duora.IntegrationTests/Participation/ParticipationGuardTests.cs`.

- [ ] Criar testes com barreiras de execução: edição confirma antes da reserva; reserva confirma antes da edição; escrita com ETag vencido; conta bloqueada enquanto aguarda guarda.
- [ ] Fazer alterações efetivas de localização — country/subdivision e regionCode legado — adquirirem a mesma guarda antes do UPDATE condicional; preservar null/omissão e sincronização dos três campos.
- [ ] Manter ordem de locks compatível com participação e incremento do marcador de segurança. Não fazer lookup de IP nessa transação.
- [ ] Edição anterior à reserva entra no snapshot; edição posterior vale para pares futuros e não encerra sala confirmada.
- [ ] Validar escritores legados da API nova. Binário antigo que escreve diretamente fora do protocolo não pode coexistir na ativação: excluir essa topologia no runbook.
- [ ] Executar integração PostgreSQL com locks reais, timeout/deadlock e unicidade de reservas. Commit após aprovação.

**Aceite:** nenhuma corrida permite reserva usando localização anterior a uma edição já confirmada sob a guarda.

## Etapa 4 — Filtro bilateral e versão de seleção

**Modificar:** `backend/src/Duora.Core/Matching/PairingAlgorithmVersion.cs`, `PairSelector.cs`, `backend/src/Duora.Infrastructure/Participation/ParticipationService.Events.cs`; testes `backend/tests/Duora.UnitTests/Matching/PairSelectorTests.cs` e `backend/tests/Duora.IntegrationTests/Participation/ParticipationTests.cs`.

- [ ] Adicionar `GreedyByPriorityV3 = 3`, preservando V1/V2 para histórico e incluindo o acordo linguístico em V3; revisar comparações explícitas com V2 no seletor e persistência.
- [ ] Depois das guardas, reler país declarado, preferência regional e comunicação. Excluir pending; projetar país apenas se declarado e restrição apenas para countries.

```text
ownValues = country declarado ? { country: código } : {}
acceptedValues = mode == countries ? { country: conjunto escolhido } : {}
par permitido = A.Accepts(B) && B.Accepts(A) && demais regras V2
```

- [ ] Testar BR→BR, BR→US sintético com aceitação bilateral, aceitação unilateral, país ausente, ambos irrestritos, pending e espera longa. Usar os construtores reais de Preferences.
- [ ] Testar caso em que ambos querem US, mas ambos declaram BR: interseção de desejados não permite o par.
- [ ] Verificar que o trecho que gera `CommunicationAgreements` reconhece V3; ausência desse ajuste não pode permitir pares sem idioma comum ou quebrar a criação da sala.
- [ ] Persistir versão executada nos registros já previstos; revisar constraints que aceitem somente versões 1/2.
- [ ] Ativação de V3 por corte controlado, com novas rodadas identificadas; não reinterpretar rodada já iniciada. Defeito após ativação pausa novos pares por `Participation__MatchingEnabled=false`, nunca reverte silenciosamente ao irrestrito.
- [ ] Executar unidade, propriedades do seletor, desempenho representativo e integração concorrente. Commit independente.

**Aceite:** regiões não mudam prioridade entre pares elegíveis; país diferente não substitui opt-in linguístico.

## Etapa 5 — Contrato de detecção sem efeitos persistentes

**Criar:** `backend/src/Duora.Contracts/Profiles/RegionDetectionContracts.cs`, `backend/src/Duora.Infrastructure/Geolocation/ICountryLookup.cs`, `backend/src/Duora.Web/Profiles/RegionDetectionEndpoints.cs`, `backend/tests/Duora.IntegrationTests/Profiles/RegionDetectionTests.cs`.

**Interfaces planejadas:** `ICountryLookup.Lookup(IPAddress address)` retorna `CountryLookupResult(Status, CountryCode?)`; `POST /api/v1/me/region-detection` retorna `status`, país habilitado se detected e `observedAt`. Estados do ADR: detected, unknown, unsupported, unavailable, disabled. Adaptador inicial de teste/inativo, não serviço remoto.

- [ ] Testar que o corpo não recebe IP; obter exclusivamente RemoteIpAddress após ReverseProxy existente.
- [ ] Testar ausência/IP não público, IPv4/IPv6, IPv4 mapeado em IPv6, proxy não confiável e excesso de saltos. Não usar país do servidor como fallback.
- [ ] Autenticação/autorização, CSRF, no-store, limitação por conta/origem e 429/Retry-After segundo o mecanismo existente; dimensionar limites no PR com teste N+1, sem inventar valor aprovado.
- [ ] Resultado US com catálogo BR-only é unsupported; não adicionar US ao catálogo para aceitar o teste. Desligado retorna disabled e não abre arquivo.
- [ ] Asserções de ausência de escrita em perfil, preferências, idiomas e histórico, em sucesso/falha/cancelamento. Nenhum IP/código por participante em logs.
- [ ] Commit com detector real desabilitado e cobertura HTTP sintética.

## Etapa 6 — Interface manual e sugestão corrigível

**Criar:** `frontend/src/features/preferences/regional-preferences-api.ts`, `RegionalPreferenceForm.tsx`, `RegionalPreferenceForm.test.tsx`, `frontend/e2e/regional-preferences.spec.ts`.

**Modificar:** `PreferencesPage.tsx` e testes, `frontend/src/features/profile/ProfilePage.tsx`, catálogos PT-BR/EN-US, `frontend/e2e/support/fake-api.ts`.

- [ ] Testar apresentação de pending, countries e unrestricted sem transformar ausência em escolha; localização permanece opcional.
- [ ] Mostrar fluxo manual completo antes de qualquer detector; não mostrar ação disponível se a capacidade estiver disabled.
- [ ] Ação de sugestão inicia requisição identificada. Pré-selecionar apenas rascunhos iniciais intocados; qualquer edição manual invalida a resposta para os respectivos campos. Navegação/cancelamento descarta observação.
- [ ] Testar resposta A atrasada após B, edição enquanto carrega, falha e unsupported: nenhuma alteração silenciosa.
- [ ] Salvar declaração pelo PATCH existente e preferência pelo PUT próprio. Usar ações de salvar independentes, com sucesso/conflito por recurso; não alegar transação entre endpoints. País declarado nunca acompanha automaticamente mudança da preferência ou vice-versa.
- [ ] Testar duas abas/ETag, rascunho preservado em 412 e gravação parcial entre recursos sem mensagem global falsa de sucesso.
- [ ] Pending bloqueia entrada em novo pareamento no servidor, além da explicação na UI. Mensagens sem revelar preferência alheia; verificar acessibilidade, PT-BR/EN-US e nenhuma mudança implícita de idiomas.
- [ ] Rodar Vitest e Playwright, atualizar simulador com as mesmas versões/semânticas do servidor. Commit independente.

## Etapa 7 — Leitor MMDB local e validade

**Pré-condição:** G2. Sem ela, apenas fixtures locais; não adicionar pacote ou baixar base real.

**Criar:** `backend/src/Duora.Core/Geolocation/DatabaseAgePolicy.cs`, `backend/src/Duora.Infrastructure/Geolocation/MmdbCountryLookup.cs`, `ActiveCountryDatabase.cs`, `CountryDatabaseManifest.cs`; testes em `backend/tests/Duora.UnitTests/Geolocation/` e `backend/tests/Duora.IntegrationTests/Geolocation/`.

- [ ] Implementar política pura de idade com TimeProvider no adaptador; epoch verificável do arquivo + manifesto, nunca LastDownloadedAt como origem da idade. Ausência/data futura invalida a promoção.

```csharp
[Theory]
[InlineData(71, true, false)]
[InlineData(72, true, true)]
[InlineData(167, true, true)]
[InlineData(168, false, true)]
public void HonorsAgeBoundaries(int hours, bool canSuggest, bool needsAlert)
{
    var published = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    var state = DatabaseAgePolicy.Evaluate(published, published.AddHours(hours));
    Assert.Equal(canSuggest, state.CanSuggest);
    Assert.Equal(needsAlert, state.NeedsAlert);
}
```

Interface planejada: `DatabaseAgePolicy.Evaluate(DateTimeOffset publishedAt, DateTimeOffset now)` retorna `(bool CanSuggest, bool NeedsAlert)`. Testar adicionalmente data futura, desconhecida e limites em segundos; data desconhecida não entra nessa função como data inventada.

- [ ] Ler somente código de país do schema de country.mmdb homologado. O pacote Lite completo usa schema distinto: teste deve rejeitar troca de produto, não inferir um campo equivalente.
- [ ] Arquivo imutável por hash e troca do leitor ativo com vida útil controlada; não invalidar handle de consulta em andamento. Conferir idade a cada consulta, mesmo com atualizador parado.
- [ ] País não mapeável vira unknown; país válido não habilitado vira unsupported na fronteira do produto. Nenhuma mudança nos rótulos CLDR.
- [ ] Testar corrupção, ausência, leitor indisponível, troca concorrente, reinício e nenhuma chamada de rede. Leitor em falha não derruba fluxo manual nem reservas.
- [ ] Registrar licença do leitor separadamente da CC BY-SA dos dados. Commit após revisão da dependência; funcionalidade desligada.

## Etapa 8 — Atualização e promoção segura

**Criar:** `backend/src/Duora.Infrastructure/Geolocation/CountryDatabaseUpdateService.cs`, `CountryDatabaseUpdateStore.cs`, `backend/src/Duora.Worker/CountryDatabaseUpdateWorker.cs`, testes `backend/tests/Duora.IntegrationTests/Geolocation/CountryDatabaseUpdateTests.cs`.

**Integração:** reutilizar conceitos de lease/fencing do ADR-0021 e schema ops, sem executar download em `ParticipationService.CompleteJobAsync`. O executor atual trata jobs de participação e usa transação de três segundos; não é local para download MMDB. Se reutilizar `ops.jobs`, separar explicitamente claim/dispatch por kind e testar que workers antigos não capturam jobs novos antes de ativar.

- [ ] Agenda durável diária por data local America/Sao_Paulo, idempotente após restart; sem acúmulo de downloads de dias perdidos.
- [ ] Agendar retries para +15min, +1h e +4h em relação à primeira falha do ciclo, sem antecipar Retry-After; documentar esse detalhe de execução no PR. Não repetir indefinidamente nem tratar 401/403/schema como transitório.
- [ ] Download com timeout/limite de bytes configurados após homologação, cancelamento e diretório exclusivo por tentativa; TLS e redirecionamentos permitidos, segredo fora de logs.
- [ ] Validação cria artefato imutável e manifesto. Manter lease durante trabalho sem transação longa; antes de ativar, usar CAS por fencing token vigente em registro autoritativo do manifesto ativo. Um rename sozinho não impede worker atrasado de promover.
- [ ] Consumidor carrega apenas artefato completo apontado por manifesto confirmado, valida hash e mantém leitor anterior até sucesso. Arquivo ausente não reativa sugestão.
- [ ] Testar dois workers, lease vencido, recuperação do worker antigo, HTTP429/5xx, arquivo truncado, disco cheio e reinício antes/depois do commit da promoção.
- [ ] Registrar origem/versão/hash/validação, nunca conteúdo da resposta de consulta. Primeira promoção exige homologação humana; mudanças de produto/schema/licença e regressões requerem revisão.
- [ ] Commit sem agendamento ativo em produção. Ativação exige G3/G5.

## Etapa 9 — Alertas, retenção e recuperação

**Criar:** `backend/src/Duora.Infrastructure/Geolocation/CountryDatabaseHealthService.cs`, `CountryDatabaseRetentionService.cs`, `CountryDatabaseRecoveryService.cs`, testes `backend/tests/Duora.IntegrationTests/Geolocation/CountryDatabaseOperationsTests.cs`; `docs/operations/ipinfo-country.md` e `docs/testing/regional-matching.md`.

- [ ] Monitor independente do atualizador: ausência de execução 36h, idade 72h, corte 168h, falhas de credencial/schema/integridade, ausência de base e tentativas esgotadas. Estados persistidos deduplicam incidentes e sobrevivem ao restart.
- [ ] Definir interface de notificação com adaptador de teste. Não escolher fornecedor/canal nem enviar alertas reais enquanto o canal estiver pendente. Canal inativo impede declarar operação pronta.
- [ ] Repetir lembrete diário só quando houver intervenção pendente; notificar mudança de gravidade e recuperação. Testar cruzamento exato de limiares e incidente persistente sem tempestade de mensagens.
- [ ] Limpeza aplica simultaneamente teto de 14 versões e 14 dias desde primeiro download daquela versão; download repetido não estende retenção. Limpeza não apaga o arquivo em uso por leitura em andamento sem descarte seguro do handle.
- [ ] Aos 14 dias remover inclusive antiga ativa; diagnóstico não autoriza uso depois de 7 dias. Excluir temporários rejeitados ao fim da execução e varrer órfãos de crashes. Metadados/alertas expiram em 90 dias.
- [ ] Cópia privada da ativa/anterior fora do host, mesma retenção. Não provisionar serviço até definição/autorização de orçamento. Restauração não inclui credenciais nem confia automaticamente em ponteiro ativo de backup antigo.
- [ ] Recuperação verifica hash/manifesto/schema/leitura/idade, usa versão validada mais recente admissível. Disponibilidade pode retomar automaticamente; integridade/licença/schema mantêm estado de bloqueio até liberação humana.
- [ ] Testar recuperação sem rede com cópia válida, sem cópia recente, manifesto adulterado, relógio incorreto, base antiga rebaixada e retomada impedida em incidente de integridade.
- [ ] Runbook registra meta de até 15 minutos com cópia válida, atendimento em um dia útil/quatro horas dentro da cobertura acordada e ensaio trimestral/após mudanças. Medir antes de afirmar meta atingida.
- [ ] Manter titular, substituto e canal explicitamente pendentes. Não criar agendamentos de chat ou monitores neste trabalho documental.

## Etapa 10 — Homologação, ativação e reversão

**Modificar documentação:** `docs/api/README.md`, `docs/architecture/modules.md`, `docs/data/model.md`, `docs/privacy/data-map.md`, `docs/testing/acceptance.md` e o runbook criado na etapa 9. Mudanças em áreas protegidas seguem revisão específica, nunca alteração lateral de política.

- [ ] Matriz de cobertura relaciona #108/ADR-0033 a unidade, PostgreSQL, HTTP, frontend/E2E e operação; ligar INV-01/02/03/05/06 às evidências reais.
- [ ] Medir migração/locks com volume sintético, verificar privilégios e ensaiar expansão/reversão preservando dados. Não aplicar migração de produção neste plano.
- [ ] Rodar gates e revisar mudanças negativas: zero novos seeds de países, zero geolocalização precisa, nenhuma alteração de idioma e nenhum log de participante.
- [ ] Entregar servidor compatível antes da SPA; preparar fluxo manual de configuração para contas existentes. Não converter perfil antigo em preferência. Definir instante de corte para novas rodadas V3, evitando mistura com binários sem guardas.
- [ ] Ativação do filtro exige G4 e autorização; pode ocorrer com detecção desabilitada. Pending impede novos pares a partir do corte; reservas anteriores não são desfeitas implicitamente.
- [ ] Ativação da detecção/atualizador exige G2/G3/G5 e autorização própria. Não iniciar operação sem nomes/canal/cobertura preparados.
- [ ] Em falha do detector: desligar somente sugestão, conservar manual. Em defeito de filtro/concorrência: pausar novos pares por MatchingEnabled, manter dados e corrigir para frente; nunca usar V2 irrestrito como rollback silencioso.
- [ ] Retorno a binário antigo exige pausa de escritas/pareamento incompatíveis, conforme ADR-0030; não remover schema nem apagar preferências.
- [ ] Issue #108 só pode encerrar após implementação e aceite efetivos; documentação/merge do plano não a concluem. US permanece excluído desta entrega.

## Comandos de validação previstos

Executar a partir da raiz do repositório, em ambiente isolado. Registrar comando, commit, resultado e limitações; os comandos abaixo não foram executados nesta elaboração.

```sh
scripts/doctor.sh
dotnet restore backend/Duora.slnx --locked-mode
dotnet build backend/Duora.slnx --no-restore
dotnet test --solution backend/Duora.slnx
dotnet format backend/Duora.slnx --verify-no-changes --no-restore
corepack pnpm --dir frontend install --frozen-lockfile
corepack pnpm --dir frontend api:generate
corepack pnpm --dir frontend typecheck
corepack pnpm --dir frontend lint
corepack pnpm --dir frontend test --maxWorkers=2
corepack pnpm --dir frontend build
corepack pnpm --dir frontend e2e
git diff --check
```

Para cada tarefa: adicionar teste que demonstra o requisito, rodar até confirmar falha esperada, implementar mínimo, rodar o grupo afetado e registrar evidência antes do commit. Não reduzir scanners/testes para liberar merge. PostgreSQL real para FKs, guardas e reservas; mocks/fixtures somente para rede e dados externos.

Antes de integrar qualquer PR: todos os checks exigidos — backend, frontend, e2e, secret-scan, contract, supply-chain, ops e imagens AMD64/ARM64 — verdes no commit revisado; merge condicionado ao head verificado. CI não comprova acesso operacional ao fornecedor ou recuperação real se esses ensaios não foram executados.

## Resultado desta elaboração

Plano revisado contra ADR-0033 e caminhos atuais do código; cinco riscos de revisão vinculados a testes nas etapas. Nenhum código de produto, configuração ou catálogo alterado. Exemplos de contratos/testes neste documento são especificações para execução posterior, não implementação. Nomes novos de arquivos são propostas; caminhos existentes foram verificados na base indicada. Não há designação de responsáveis ou canal, nem habilitação dos EUA.

## Registro de execução de 21/10/2026 — somente etapas 1 e 2

O responsável autorizou a implementação das etapas 1 e 2 e a abertura de PR. Essa autorização substitui,
para essas duas etapas, a pendência de execução em G0; não autoriza executar etapas 3 a 10.

A entrega para revisão adiciona modelo validado, migração aditiva com consistência diferida, API privada
GET/PUT com versão regional própria, guarda compartilhada e contratos gerados. Não ativa filtro no matching,
não altera a localização declarada, não cria UI/detector/atualizador e não habilita US. Titular, substituto
e canal de alertas continuam pendentes. A issue permanece aberta; merge, aplicação da migração e implantação
seguem os gates e autorizações próprios. O corpo das etapas conserva o plano original aprovado.

## Registro de execução de 21/10/2026 — etapas 3 e 4

O responsável autorizou as etapas 3 e 4 e abertura de PR. Essa autorização sucede a entrega das etapas 1–2
integrada no PR #143 e libera G0 somente para a entrega B; não autoriza as etapas 5–10 nem ativação.
A entrega serializa a localização moderna/legada, projeta preferências bilaterais por país após guardas e
prepara V3 com versão persistida por rodada. Corte opcional sem configuração por padrão; rodadas existentes
não são reinterpretadas. A migração expande a aceitação V2/V3 e vincula versão do par à rodada, sem backfill.
Ver [runbook do corte](../../operations/regional-matching.md) e [matriz de testes](../../testing/regional-matching.md).
Responsáveis nominais/canal pendentes; sem detector, UI, MMDB, EUA ou implantação. A issue segue aberta.

## Continuação autorizada — interface manual

Após auditoria e integração do PR #143 (`787b9b9`), o responsável autorizou em 21/10/2026
avançar nas guardas, no filtro bilateral e na interface manual. As etapas 3–4 têm entrega
própria em andamento; este PR implementa somente a parte manual da etapa 6 sobre a API
já integrada. A #108 permanece aberta. Detector, MMDB, atualização, operação, novos países
e ativação do filtro não integram esta entrega.

A interface conserva localização declarada e preferência em recursos e salvamentos independentes.
O perfil manual já existente não é alterado. Não há botão de detecção enquanto essa capacidade
estiver ausente. Pending requer ação explícita; 412 conserva o rascunho e exige recarregar a versão
antes de nova confirmação. Nenhum consentimento é preenchido para usuários antigos. Catálogo BR.

Enquanto a publicação permanecer V2, a interface informa visivelmente que salvar a preferência
prepara a escolha e ainda não restringe os pareamentos atuais. No corte G4, atualizar essa indicação
junto da ativação e da comunicação aos participantes. Workers incompatíveis não podem coexistir
com V3. Em defeito após ativação, pausar matching e corrigir para frente, sem apagar preferências.


## Registro de execução de 21/10/2026 — etapas 5 e 6

Autorizadas explicitamente as etapas 5 e 6, reutilizando a interface manual do PR #146. O adaptador registrado
é sempre `DisabledCountryLookup`: não há chave de configuração que ligue um leitor real, acesso a arquivo,
MMDB, download, serviço externo ou seed US nesta entrega. G2–G5 permanecem como pré-requisitos das entregas futuras.
Responsáveis nominais e canal de alertas continuam pendentes; o corte V3 continua sem valor configurado.

- `GET /api/v1/me/region-detection/capability` informa somente a disponibilidade, sem consultar a conexão.
  Sem capacidade, a UI mantém o fluxo manual e não oferece ação de detecção. Falha dessa leitura também não a oferece.
- `POST /api/v1/me/region-detection` aceita somente corpo vazio e usa exclusivamente `RemoteIpAddress` após o
  middleware existente. Não percorre X-Forwarded-For. Uma cadeia residual após o salto confiável é inconclusiva.
  Estados: detected (somente BR habilitado), unknown, unsupported (inclui US sintético), unavailable, disabled.
  `observedAt` usa o relógio do servidor; não há escrita em perfil, preferência, idiomas, guardas ou histórico de pares.
- Hipótese inicial revisável de limites, proposta neste PR: 6/minuto por conta e 60/minuto por origem, sem fila;
  conta impede renovação por troca de IP, origem mais alta acomoda NAT. Usa o mecanismo local existente, IPv6 /64,
  429/Retry-After e reinicia com o processo. Testes N+1 isolam ambas as cotas; não é orçamento operacional homologado.
  Antes de ativar um detector real, medir carga e impacto de NAT, revisar valores/cobertura e obter aprovação operacional.
- Admissão de IP deliberadamente conservadora: IPv4 público e IPv6 global unicast 2000::/3, recusando endereços
  especiais, documentação e transição. IPv4 mapeado é normalizado. Exceções globais de blocos especiais não geram
  sugestão; não há fallback por locale, fuso ou país do servidor. Conferência: registros
  [IPv4 IANA](https://www.iana.org/assignments/iana-ipv4-special-registry/) e
  [IPv6 IANA](https://www.iana.org/assignments/iana-ipv6-special-registry/).
- O componente comum de sugestão é usado nos formulários existentes de perfil e preferência. Só a ação explícita
  faz POST; resposta fica no componente montado, sem cache/localStorage. Cada pedido tem geração e AbortController;
  novo pedido, cancelamento e saída descartam o anterior. Toda edição manual regional, mesmo revertida, invalida a
  pré-seleção; região já salva nunca é substituída, inclusive null ("não informar"). Como não existe metadado de
  origem para diferenciar null manual de ausência, somente perfil não salvo (versão 0) admite pré-seleção. Um perfil
  existente sem país usa escolha manual; não acrescentar rastreamento para inferir a intenção. Nenhuma leitura ou
  salvamento sincroniza automaticamente os recursos.
- Declaração continua pelo PATCH/ETag do perfil; preferência pelo PUT/ETag próprio. Mensagens e conflitos pertencem
  a cada formulário, com rascunho preservado em 412. Sugestão não grava, não altera idiomas e não ativa o filtro.
- Cobertura: HTTP sintético de IPv4/IPv6, proxies, autenticação/CSRF/corpo, N+1 e snapshots PostgreSQL em
  sucesso, desconhecido, indisponibilidade e cancelamento; MSW/Vitest de respostas atrasadas, manual/salvo,
  cancelamento e navegação; Playwright PT-BR/EN-US e acessibilidade, com gravação parcial entre recursos.

Não implementados: etapas 7–10, detector real, atualizador, alertas operacionais, implantação, ativação do filtro ou EUA.


## Registro de execução de 21/10/2026 — etapas 7 e 8

Autorização do responsável: implementar estas duas etapas sem ativar o detector nem habilitar US.
Aprovação adicional explícita: MaxMind.Db 5.2.0, Apache-2.0. G2 fica parcialmente resolvido apenas quanto à
biblioteca; arquivo IPinfo real, schema/data/cadência, autenticação, redirecionamentos e limites continuam pendentes.
O texto original acima registra o plano aprovado, não substitui este estado de execução.

- Preparados leitor local, validade por epoch, manifesto verificável, troca de geração e adaptador desligado.
- Preparados agenda durável, retries relativos à primeira falha, download limitado, validação sintética,
  lease renovável, fencing e publicação imutável antes do commit do ponteiro.
- Adicionado tipo exclusivo em ops.jobs e tabelas Operations aditivas; nenhum job é criado pela migração.
- Web mantém DisabledCountryLookup; WorkerHost não registra o novo executor. Nenhum volume, token ou base é provisionado.
- Fixtures próprias cobrem IPv4/IPv6, corrupção, datas, restart, concorrência, HTTP e armazenamento.
- Retenção, alertas, recuperação administrativa e ensaios reais continuam na etapa 9; não são capacidade operacional comprovada.

Decisões de execução: consumidor singleton separado de Participation; cache de decodificação limitado a 16 entradas;
códigos ISO validados conservadoramente pela BCL sem inferir cultura; redirecionamentos limitados a três e sem
repassar Authorization. Valores de bytes/tempo/redes e allowlist não recebem defaults operacionais.
São pré-requisitos futuros: storage privado compartilhado, relógio confiável e homologação real. Nenhum recurso é criado.

G3 (titular, substituto e canal) e G5 permanecem abertos. Issue #108 aberta; BR + 27 UFs; sem implantação, detector ativo ou US.
Consulte o [ADR](../../architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md#execução-autorizada-das-etapas-7-e-8--21102026)
e a [preparação operacional](../../operations/ipinfo-country.md). Evidência de execução efetiva fica no PR e no CI.
