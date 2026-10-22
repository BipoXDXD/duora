# Contrato IPinfo Lite — preparação inativa da #108

**Escopo:** downloader, manifesto, validador e leitor local do [ADR-0037](../../architecture/adr/0037-ipinfo-lite-mmdb-para-sugestao-de-pais.md).
**PR:** [#183](https://github.com/BipoXDXD/duora/pull/183). G2 e operação permanecem pendentes.

## Evidências executadas

- Commit de testes `3222851`: build remoto passou; formatação recusou dois blocos condicionais antes da execução
  dos testes. Corrigidos sem alterar gates ou workflows; não conta como RED funcional.
- Commit de testes `2bb7d5301c280d95f8ae8a8c096a9bdac673c430`:
  [CI RED](https://github.com/BipoXDXD/duora/actions/runs/39734850625), comando
  `dotnet test --solution backend/Duora.slnx --no-build`: **3.062 testes, 3.057 passaram, 5 falharam, zero ignorados**.
  Compilação/formatação passaram; as cinco falhas correspondem às regressões esperadas antes do código produtivo:
  `InitialRequestUsesLiteOriginWithoutQueryCredentials`,
  `LiteCountryCodeIsUsedForIpv4Ipv6AndMappedAddressesWithoutProjectingAsn`,
  `CountryCodeAloneIsSufficientWithoutEnrichmentFields`,
  `LegacyRecordsAreRejectedEvenWithAnApprovedLiteDatabaseType` e
  `LiteManifestIdentityIsExplicitAndLegacyManifestCannotBeReused`.
- Verificação local leve: `git diff --check`. Nenhum restore/build/.NET test, Docker ou suíte pesada no Mac.
- Resultado do head de implementação: **pendente de CI**; resultados anteriores não o certificam.

## Contrato preparado e limites

Origem Lite fixa; identidade explícita `ipinfo_lite.mmdb` / `ipinfo-lite-country-v1` / CC BY-SA 4.0;
leitura apenas de `country_code`. Manifestos antigos/incompletos e schema legado não recebem rótulo Lite.
BCL valida códigos de forma conservadora; seus resultados ficam em cache limitado a 676 combinações ASCII,
sem armazenar IPs, redes de consultas ou enriquecimento. Testes de atualização, recuperação e saúde usam
fixtures Lite completas e mantêm aprovação de hash, lease/fencing, época/idade e recusa de metadados antigos.

Sem mudança de contratos HTTP, schema PostgreSQL, catálogo, idioma, preferência, notificadores ou registros
nos hosts. Web continua com `DisabledCountryLookup`; atualizador/monitor seguem desativados. V3 inativo,
BR + 27 UFs, sem EUA ou alertas reais. A #108 continua aberta.

Não foram usados banco IPinfo real, credenciais, IPs de participantes ou acesso ao fornecedor. Não demonstrados:
precisão, autenticação/redirects reais, `DatabaseType`, recursos/limites, primeiro hash ou capacidade operacional.
O perfil sintético não é default de produção. G2/G3/G4/G5 e autorizações de implantação/ativação continuam próprios.
