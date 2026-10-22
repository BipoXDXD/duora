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

## Revalidação após atualização da main — 22/10/2026

Branch atualizada sobre `f99456549baa85ae02127e50eda2d4de09d7c423`, sem conflitos. O `range-diff`
confirmou que o patch Resend/MFA permaneceu idêntico no rebase. Restore com lockfile, build Debug,
27 casos Resend e 140 testes frontend de autenticação passaram novamente. A CI dessa base executou
3.222 testes de backend sem falhas/skips e aprovou os demais gates concluídos.

Durante o E2E houve uma falha intermitente de T-19 (`profile.spec.ts`) que passou no retry. O helper
retornava assim que o título aparecia, antes da leitura inicial do perfil. A simulação de edição por outra
aba podia então anteceder a leitura da versão-base e impedir o conflito 412 que o teste pretendia criar.
Uma cópia diagnóstica com atraso de um segundo apenas na primeira leitura reproduziu a ausência do alerta.
A correção aguarda o campo de nome ficar visível antes de retornar do helper, sem pausas fixas, aumento de
timeout ou mudança nas asserções de conflito, preservação do rascunho, releitura e reaplicação.

A cópia diagnóstica corrigida passou em 10 repetições. A suíte de perfil original passou em 30 execuções
(5 repetições dos 3 cenários, em Chromium e mobile Chromium), com `--retries=0`. Lint, Prettier e typecheck
passaram. O teste original também havia passado em 20 repetições sem atraso, demonstrando por que a corrida
precisou de uma leitura controlada para reprodução. A cópia e o atraso de diagnóstico não fazem parte do PR.
Os logs e cópias para reprodução ficam na evidência local `outputs/duora-resend-main-sync` do chat.

O E2E remoto também sofreu cancelamento após 19 min 15 s de download de pacotes Ubuntu; isso é separado da
corrida no fixture. O resultado final dos nove gates do novo commit deve ser conferido na CI vinculada na
descrição do PR. Nenhum workflow ou gate foi alterado. Nenhum merge, acesso à VPS ou deploy foi executado.

## Precisão do relógio dos testes de timeout

A CI posterior encontrou um falso negativo no teste do prazo total do login: 503 correto com `Retry-After`
e sem sessão, mas o Stopwatch mediu `999,9478 ms` e a asserção exigia 1.000 ms sem a resolução do timer.
No .NET 10 em Linux/macOS, os timeouts usam um contador inteiro de milissegundos
([TimerQueue.Unix](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/TimerQueue.Unix.cs#L7),
[agendamento](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/Timer.cs#L313)).

Os limites inferiores dos dois testes de timeout agora usam `Environment.TickCount64`, capturado antes e
logo após a requisição, mantendo o mínimo de 1.000 ms. Stopwatch continua medindo o teto de 10 s e o
diagnóstico. Permanecem os checks de 503, `Retry-After`, sessão ausente e teto da fila. O piso de segurança
contra enumeração e suas asserções de alta resolução não foram alterados; nenhum código de autenticação
integra esta correção.

Controle negativo em ambiente local descartável: reduzir temporariamente os dois prazos pela metade fez
os três casos rejeitarem respostas em torno de 500 ms. Os arquivos de produto foram restaurados byte a byte
antes do build positivo. Os 23 casos de `LoginConcurrencyTests` e 15 repetições dos casos afetados passaram,
sem skips. Não foi inserida tolerância arbitrária, pausa, aumento de timeout ou retirada de asserção.
A CI completa do commit final continua sendo obrigatória e está vinculada na descrição do PR.
