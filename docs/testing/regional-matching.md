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
