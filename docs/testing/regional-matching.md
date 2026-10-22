# Validação regional — etapas 1–4 da issue #108

Referência: [ADR-0033](../architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md).
Ativação e rollback: [procedimento do corte V3](../operations/regional-matching.md).

| Regra | Evidência automatizada |
|---|---|
| API privada, ETags independentes, ausência pending, consistência/FKs | RegionalPreferenceTests, RegionalPreferenceContractTests, RegionalPreferenceMigrationTests |
| Localização moderna e legada aguardam a guarda; versão/conta relidas, timeout sem escrita | ProfileLocationGuardTests, OwnProfileTests |
| Edição antes/depois da guarda; sala confirmada preservada | Participation.RegionalMatchingTests (modern, legacy e preference) |
| País declarado, bilateralidade, ausência, pending, unrestricted, nenhuma inferência de consentimento | Matching.RegionalMatchingTests e Participation.RegionalMatchingTests |
| BR/US sintéticos, ambos querem US mas declaram BR, espera não relaxa filtros | Matching.RegionalMatchingTests; códigos US são somente fixtures do domínio, nunca seeds |
| V3 mantém idioma comum, opt-in entre locales, bloqueio, elegibilidade e histórico | Matching.RegionalMatchingTests, Participation.RegionalMatchingTests e suítes existentes |
| Determinismo, aceitação bilateral, maximalidade e uma reserva por pessoa (INV-02/03/06) | Propriedades aleatórias V3 (200 casos), testes PostgreSQL com coordenadores concorrentes e eventos sobrepostos |
| Mesmo ranking e orçamento de 50 ms/100 candidatos, menor de cinco execuções após aquecimento | PairSelectorPerformanceTests para V1 e V3; cenários regionais positivos/negativos em RegionalMatchingTests |
| Expansão conserva todas as linhas V2, catálogo e salas; versão do par coincide com a rodada | RegionalMatchingMigrationTests |
| Corte opcional, início no limite, rodada persistida não reinterpretada, retry/restart | Participation.RegionalMatchingTests |

Execução na raiz, com Docker e dependências fixadas:

```sh
dotnet test --project backend/tests/Duora.UnitTests -- --filter-class '*Matching*'
dotnet test --project backend/tests/Duora.IntegrationTests -- --filter-class '*Regional*' --filter-class '*ProfileLocationGuardTests' --filter-class '*ParticipationGuardTests'
dotnet test --solution backend/Duora.slnx
dotnet format backend/Duora.slnx --verify-no-changes --no-restore
corepack pnpm --dir frontend api:generate
corepack pnpm --dir frontend typecheck
git diff --check
```

O relatório do PR registra os resultados efetivamente obtidos, inclusive tentativas iniciais com falha;
estes comandos e a matriz não são evidência de execução por si. Testes usam PostgreSQL descartável,
dados sintéticos e construtores reais de Preferences. Nada comprova acesso ao fornecedor, precisão IP,
alertas, recuperação ou desempenho sob volume operacional; esses ensaios dependem das próximas etapas.
Sem detector, base real, coleta de IP, UI regional, ativação de V3, implantação ou US habilitado.
Titular/substituto e canal de alertas permanecem pendentes. A issue #108 continua aberta.


## Contrato inativo e sugestão temporária — etapas 5 e 6

`RegionDetectionTests` usa somente resultados sintéticos e PostgreSQL descartável. Verifica autenticação,
CSRF, recusa de corpo com IP, no-store, IP ausente/não público, IPv4/IPv6/mapeado, proxy confiável de um salto,
cadeia residual e cabeçalho de remetente não confiável. US resulta em unsupported sem país exposto.
N+1 separa limite de conta e origem; snapshots de perfil, preferências, idiomas, locale, guarda e histórico
permanecem iguais em sucesso, unknown, unavailable e cancelamento. Captura de logs recusa IP e país da observação.
O cancelamento em TestServer pode propagar OperationCanceledException ou ser tratado pelo middleware como 499;
ambos exigem lookup alcançado, token cancelado e snapshot preservado.

Vitest exercita formulários existentes e POST real no cliente HTTP via MSW: pré-seleção sem escrita, manual
mesmo após voltar ao valor inicial, A após B, cancelamento/saída, preferência salva, localização salva como null e estados sem sugestão.
Playwright cobre PT-BR/EN-US nos três projetos, acessibilidade e sucesso do perfil com conflito independente
na preferência. O simulador oferece capacidade false por padrão, como o servidor; fixtures habilitadas são
exclusivas dos testes. As coberturas anteriores de 412/duas abas e pareamento V3 continuam obrigatórias.

Sem ensaio real IPinfo/MMDB: leitor/atualizador e operação ainda não foram implementados/autorizados nesta entrega.


## Leitor e atualização inativos — etapas 7 e 8

A biblioteca aprovada lê MMDB construído pelos próprios testes com redes reservadas de documentação. Não há banco
real IPinfo nem requisição ao fornecedor. PostgreSQL continua real e descartável; transporte usa HttpMessageHandler sintético.

| Contrato | Testes |
|---|---|
| 72h/168h em segundos, data ausente/futura, agenda 06h e retries relativos | DatabaseAgePolicyTests, CountryDatabaseScheduleTests |
| IPv4/IPv6, unknown, corrupção, produto/schema, epoch, refresh atrasado e leituras concorrentes | CountryDatabaseReaderTests |
| Dois claims, lease vencido, renovação, Retry-After, reinício e metadados originais | CountryDatabaseUpdateTests |
| TLS/allowlist, segredo não repassado, redirects limitados, 401/403/429/5xx, bytes, timeout, disco | CountryDatabaseDownloadTests |
| Artefato completo antes de ponteiro, homologação do primeiro hash, truncamento e worker atrasado | CountryDatabasePipelineTests |
| Renovação durante I/O sem locks SQL, perda de posse e executor desligado | CountryDatabaseWorkerTests |

Os testes de migração/privilégios, arquitetura e contratos HTTP existentes continuam obrigatórios, assim como todas
as suítes do CI. A role app só insere/lê manifestos; os novos estados pertencem a Operations. Os executores antigos
não podem capturar o novo kind. O relatório do PR diferencia resultados locais anteriores do resultado do head no CI.

Não executar a suíte Docker em paralelo num Mac de 16GB. Para esta entrega, após o travamento relatado pelo responsável,
validação final ocorre no CI remoto; comandos anteriores e resultados são registrados sem atribuí-los ao head posterior.

```sh
dotnet test --project backend/tests/Duora.UnitTests -- --filter-class '*Geolocation.*'
dotnet test --project backend/tests/Duora.IntegrationTests -- --filter-class '*Geolocation.*'
```

Homologação do arquivo real, relógios entre hosts/restarts, download/autenticação reais, armazenamento/ACLs, alertas,
retenção e recuperação operacional continuam pendentes. Testes sintéticos não demonstram precisão nem capacidade operacional.


## Operação inativa — etapa 9

`CountryDatabaseOperationsTests` usa relógio sintético, PostgreSQL real descartável, MMDB próprio e adaptadores
em memória. Cobre 36h/72h/168h exatas, incidentes persistentes, poll concorrente, lembretes/recovery, retry de
notificação, falhas terminais/tentativas esgotadas, retenção simultânea 14 versões/14 dias e metadados 90 dias,
cópia privada limitada a duas versões, restauração sem rede, cópia adulterada/antiga/não aprovada, relógio atrás
da marca persistida, revogação local e publicador atrasado concorrente com retenção. Falta de cópia mais recente
não autoriza downgrade. Testes de privilégios confirmam que a aplicação não ganha DELETE em registros imutáveis.

Os comandos de restore/build/format/test backend, frontend, contrato, e2e, ops, scanners e imagens são os gates
existentes do CI. **Nenhum teste pesado, build ou Docker é executado no Mac de 16GB nesta etapa.** O relatório
registra o head e a execução remota; comandos documentados não equivalem a resultado. Adaptadores sintéticos
não comprovam Gmail real, base IPinfo, retenção fora do host, SLA de 15 minutos ou cobertura operacional.

Titular/Gmail privado já designados por decisão posterior; substituto, contingência/cobertura, transporte,
armazenamento/orçamento e homologação G2/G3/G5 continuam pendentes. Nenhum serviço é registrado, detector ou
atualizador ativado, alerta real enviado ou país habilitado. #108 aberta. Ver [runbook](../operations/ipinfo-country.md).

A regressão de liberação humana verifica que autorização de reparo não passa a uma promoção posterior.
Casos de manifesto adulterado abrangem origem, tipo inválido e cronologia de primeiro download, com alerta e bloqueio;
a validação estrutural é compartilhada entre admissão, monitor e recuperação, preservando o corte de 168h.


## Homologação integrada — etapa 10

| Requisito / invariante | Evidência executável (suíte existente salvo indicação) |
|---|---|
| INV-01: conta/idiomas/consentimento continuam obrigatórios | `RegionalMatchingTests.V3KeepsAllOtherEligibilityFilters`; `RegionalPreferenceGuardTests.AccountAndVersionAreRecheckedAfterAcquiringTheSharedGuard` |
| INV-02: reserva única concorrente | `RegionalMatchingTests.ConcurrentCoordinatorsAndOverlappingV3EventsReserveOnlyOnce` |
| INV-03: bloqueio e localização serializados | `V3KeepsAllOtherEligibilityFilters` (block); `EditsCommittedBeforeGuardsAreObservedAndLaterEditsPreserveConfirmedRooms` (modern/legacy/preference) |
| INV-05: preferência privada e sugestão sem efeitos | `RegionalPreferenceContractTests`, `RegionDetectionTests` (autorização, snapshots e logs); `ProfilePage.test.tsx`; `regional-preferences.spec.ts` |
| INV-06: versão, concorrência e retry | `RegionalPreferenceTests`, `RegionalPreferenceGuardTests`, `RegionalPreferenceTimeoutTests`; `PersistedV3UsesExplicitChoicesAndDeclaredCountries` |
| País bilateral, desconhecido/pending, subdivisão e histórico | `RegionalMatchingTests`; testes unitários de Profiles e Matching; sem relaxamento por espera |
| Migração/locks/volume e reversão preservadora | Novo `RegionalRolloutRehearsalTests` (10.000 perfis); `RegionalMatchingMigrationTests`; `AppRolePrivilegesTests` |
| Corte persistido e reinício | `CutoverOnlyLabelsNewRoundsAndNeverReinterpretsAnExistingRound`; execução V3 com serviço sem corte configurado |
| Fluxo manual e conflito independente, dois idiomas | `ProfilePage.test.tsx`, `profile-form.test.ts`, `profile-location.test.ts`, `regional-preferences.spec.ts`, `profile.spec.ts` |
| Proxy confiável, IP desconhecido/VPN, disabled, no-store | `RegionDetectionTests`: VPN não tem bypass nem precisão garantida; somente sugestão, nunca identidade |
| Atualização/alertas/retenção/recuperação inativos | `CountryDatabaseReaderTests`, `CountryDatabasePipelineTests`, `CountryDatabaseWorkerTests`, `CountryDatabaseOperationsTests`; rede/notifier/archive sintéticos |

O ensaio novo caracteriza contratos já implementados; não modifica código de produção para fabricar falha.
Mede expansão e contenção no PostgreSQL real do CI. Não executa binário antigo nem prova throughput de matching,
precisão IPinfo, transporte de alertas ou capacidade do host. Esses aceites permanecem nos gates operacionais.
Todos os nove jobs obrigatórios devem passar no mesmo head; [resultados e limitações](reports/2026-10-22-regional-homologation.md).
