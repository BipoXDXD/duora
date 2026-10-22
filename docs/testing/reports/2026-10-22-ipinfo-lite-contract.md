# Contrato IPinfo Lite — preparação inativa da #108

**Escopo:** downloader, manifesto, validador e leitor local do [ADR-0037](../../architecture/adr/0037-ipinfo-lite-mmdb-para-sugestao-de-pais.md).
**PR:** [#183](https://github.com/BipoXDXD/duora/pull/183). G2 e operação permanecem pendentes.

## Evidências executadas

- Commit de testes `3222851`: build remoto passou; formatação recusou dois blocos condicionais antes da execução
  dos testes. Corrigidos sem alterar gates ou workflows; não conta como RED funcional.
- Commit de testes `2bb7d5301c280d95f8ae8a8c096a9bdac673c430`:
  [backend RED](https://github.com/BipoXDXD/duora/actions/runs/39734850625/job/119678344273), comando
  `dotnet test --solution backend/Duora.slnx --no-build`: **3.062 testes, 3.057 passaram, 5 falharam, zero ignorados**.
  Compilação/formatação passaram; as cinco falhas correspondem às regressões esperadas antes do código produtivo:
  `InitialRequestUsesLiteOriginWithoutQueryCredentials`,
  `LiteCountryCodeIsUsedForIpv4Ipv6AndMappedAddressesWithoutProjectingAsn`,
  `CountryCodeAloneIsSufficientWithoutEnrichmentFields`,
  `LegacyRecordsAreRejectedEvenWithAnApprovedLiteDatabaseType` e
  `LiteManifestIdentityIsExplicitAndLegacyManifestCannotBeReused`.
- Verificação local leve: `git diff --check`. Nenhum restore/build/.NET test, Docker ou suíte pesada no Mac.
- Commit de implementação `a6ef3a7`: build remoto passou; formatação apontou duas quebras de linha no
  inicializador do manifesto legado em `CountryDatabaseUpdateTests`. Corrigidas sem alterar lógica ou gates.
- Commit `56034e9`: [backend GREEN](https://github.com/BipoXDXD/duora/actions/runs/39737790269/job/119687702240),
  **3.068 testes passaram, zero falhas/ignorados**; build e formatação também passaram.
  A mesma execução falhou em `ops`, no teste existente
  `DualNetworkUpstreamTests.test_api_webhooks_and_hubs_reach_the_duora_upstream`: primeira requisição HTTPS
  recebeu alerta TLS 80, enquanto as seguintes passaram. A fixture aguardava somente HTTP, sem exigir TLS.
  A falha foi investigada e corrigida na espera de prontidão, sem alterar assertivas, filtros ou workflows.
- Regressão leve da fixture Caddy: `PYTHONPATH=scripts/deploy python3 -m unittest test_caddy_readiness -v`.
  A condição antiga reproduziu duas falhas em três casos; após exigir HTTP e HTTPS com resposta do upstream
  privado, **3/3 passaram**, sem Docker, rede ou espera real. A integração Caddy real continua obrigatória no CI.
  Revisão do delta identificou um `import time` ainda necessário ao outro teste da classe; restaurado.
  Observação menor preservada: 30s é a janela para iniciar sondas; o timeout individual de transporte pode
  levar uma sonda já iniciada além dela, como ocorria antes para HTTP. Não é SLA nem mudança em produção.
- **Resultados finais:** consultar os [checks do PR #183](https://github.com/BipoXDXD/duora/pull/183/checks)
  e o registro final do head exato no PR. Este arquivo registra o ciclo inicial; não antecipa resultado
  de uma execução ainda em andamento nem transfere evidência entre commits.

## Contrato preparado e limites

Origem Lite fixa; identidade explícita `ipinfo_lite.mmdb` / `ipinfo-lite-country-v1` / CC BY-SA 4.0;
leitura apenas de `country_code`. Manifestos antigos/incompletos e schema legado não recebem rótulo Lite.
BCL valida códigos de forma conservadora; seus resultados ficam em cache limitado a 676 combinações ASCII,
sem armazenar IPs, redes de consultas ou enriquecimento. Testes de atualização, recuperação e saúde usam
fixtures Lite completas e mantêm aprovação de hash, lease/fencing, época/idade e recusa de metadados antigos.

O ajuste incidental da fixture Caddy adiciona somente uma condição de prontidão do teste: HTTP e TLS devem
responder corretamente antes das assertivas de isolamento, no mesmo deadline. Não adiciona retry às
assertivas, não aceita upstream estrangeiro e falha se TLS não ficar pronto; regressões cobrem esses casos.

Sem mudança de contratos HTTP, schema PostgreSQL, catálogo, idioma, preferência, notificadores ou registros
nos hosts. Web continua com `DisabledCountryLookup`; atualizador/monitor seguem desativados. V3 inativo,
BR + 27 UFs, sem EUA ou alertas reais. A #108 continua aberta.

Não foram usados banco IPinfo real, credenciais, IPs de participantes ou acesso ao fornecedor. Não demonstrados:
precisão, autenticação/redirects reais, `DatabaseType`, recursos/limites, primeiro hash ou capacidade operacional.
O perfil sintético não é default de produção. G2/G3/G4/G5 e autorizações de implantação/ativação continuam próprios.
