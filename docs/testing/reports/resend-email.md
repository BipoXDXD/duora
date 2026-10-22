# Homologação descartável — Resend e foco do MFA

Data: 22/10/2026. Base: `cd4a908` (main). macOS ARM64, .NET 10, Node 24,
Docker Desktop com PostgreSQL 18.6 real. Sem acesso à VPS, segredos, contas ou dados de produção.

## Resultado

- Build da solução e restore com lockfile: PASS. Permanecem três avisos existentes de licença SixLabors
  no build Debug; não foi validado build Release com licença neste laboratório.
- `dotnet format backend/Duora.slnx --verify-no-changes --no-restore`: PASS.
- Executável `Duora.IntegrationTests.dll -class 'Duora.IntegrationTests.Outbox.*' -class 'Duora.IntegrationTests.Identity.*' -class Duora.IntegrationTests.WorkerHostTests -maxThreads 4`: **705 PASS**, zero falhas/skips.
  Inclui 27 casos novos do Resend: contrato HTTP, configuração, códigos de erro, redirects recusados,
  confirmação inválida, cancelamento, timeout, dois idiomas, fila e autenticação completas, retry e concorrência.
- Executável `Duora.UnitTests.dll`: **1.573 PASS**; `Duora.ArchitectureTests.dll`: **35 PASS**.
- `pnpm --dir frontend typecheck`, `lint`, `format:check`, `build`: PASS.
- `pnpm --dir frontend test --maxWorkers=2`: **841 PASS**.
- `pnpm --dir frontend exec vitest run src/features/auth --maxWorkers=2`: **140 PASS** após restauração do controle negativo.
- `pnpm --dir frontend exec playwright test e2e/auth.spec.ts e2e/session-privacy.spec.ts --project=chromium --project=mobile-chromium --workers=2`: **54 PASS**, execução headless.
- Renderização de Compose base + override com valores sintéticos: PASS; demais serviços, volumes e redes
  idênticos, chave como arquivo secreto somente no worker. Nenhum `compose up` de produção.
- OpenAPI e código da interface de MFA: sem alteração.

O teste original de foco passou isoladamente neste turno, mas tinha falha intermitente reproduzida na
homologação anterior. Controle negativo local: retirar temporariamente o efeito de foco fez a asserção
corrigida falhar; o arquivo foi restaurado integralmente. Não foi introduzido sleep, skip nem redução do teste.
Os primeiros testes novos expuseram a ausência do adaptador antes da implementação. Na construção do novo
harness, expectativas antigas do assunto de confirmação foram ajustadas ao contrato vigente de código de
8 dígitos; os testes finais usam código e token recebidos no HTTP, não extraídos do banco.

## Escopo e limites

O PostgreSQL e os receptores HTTP são descartáveis e usam apenas dados sintéticos. O adaptador e a
composição do worker são reais; o serviço Resend remoto é substituído somente no teste por um receptor
loopback. Não foi validada entrega em caixa postal, DNS do remetente, chave real, quota ou webhooks.
Os fluxos HTTP de conta usam o TestServer existente; os 54 testes de navegador usam os mocks da suíte E2E.
A suíte completa de integração (incluindo isolamento de mídia Linux) e WebKit ficam para a CI Linux,
sem reduzir nenhum gate. Nenhum workflow, teste de arquitetura, autorização ou política foi alterado.

Consultar [runbook](../../operations/resend.md) para configuração, 24 h de idempotência, interpretação de
`Delivered`, ativação posterior e rollback. Não consumir pendências externas com pickup durante rollback.
Não houve instalação, migração ou publicação em produção, nem envio externo de email.

A revisão humana deve conferir especialmente configuração do remetente/segredo, janela de deduplicação,
tratamento de respostas e a distinção entre aceite do provedor e entrega. Não foram identificadas instruções
externas maliciosas. A CI do PR é a evidência autoritativa dos gates completos e deve terminar verde antes de merge.
