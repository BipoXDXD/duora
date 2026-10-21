# Duora issue 93 — plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for execução direta, ou superpowers:subagent-driven-development se o responsável escolher execução por subagentes. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** consolidar somente os deltas compatíveis dos PRs 76, 87, 88, 91, 92 e 94, eliminando a dependência etária e implementando confirmação por e-mail e selfie de presença/qualidade.

**Architecture:** Identity mantém decisão pura e persistência transacional; Infrastructure encapsula AWS, sem SDK no Core. Worker consulta resultados com lease e idempotência; SPA captura com Amplify e nunca determina aprovação. Infraestrutura e política linguística são entregas separáveis, sem implantação.

**Tech Stack:** .NET 10, EF Core/Npgsql, PostgreSQL 18.6 em Testcontainers, React/TypeScript, Vitest, Playwright e Docker; SDKs AWS somente após revisão de licença e versão.

**Spec:** `docs/superpowers/specs/2026-10-19-consolidacao-93-design.md`, aprovada pelo responsável em 19/10/2026.

## Global Constraints

- Não solicitar documentos, inferir idade ou exigir SMS.
- Manter sanções independentes, MFA por papel, privilégio mínimo, proteção contra enumeração e pré-sequestro de contas.
- Código: oito dígitos ASCII, geração criptográfica, 15 minutos.
- Sem OutputConfig/S3 e com AuditImagesLimit=0.
- Outbox leva somente identificadores no fluxo de selfie.
- Não aplicar DROP, apagar evidência ou executar migração destrutiva.
- Nenhuma integração ou implantação antes de revisão humana e gates.
- Preservar alterações locais do checkout original, branches antigas, workflows, CODEOWNERS e políticas protegidas.
- Base de execução: worktree `work/duora`, branch `maintenance/consolidation-93`, main-base `92150065`; conferir divergência remota antes de publicar.

## Review Focus

1. Recadastro/reenvio concorrente: código anterior não confirma nem recupera orçamento esgotado — tarefa 1.
2. Conta restrita por múltiplos motivos: retirada da idade não remove sanção independente — tarefa 3.
3. Resultado tardio após exclusão/suspensão ou sessão trocada: não ativa conta — tarefas 2 e 4.
4. Expiração da sessão externa entre captura e consulta: recaptura controlada, sem cobrança infinita — tarefa 4.
5. Câmera incompatível, redução de movimento e tecnologia assistiva: mensagem acionável sem documento nem aprovação automática — tarefa 5.

## Entregas e ordem

Executar tarefas 1–6 em sequência numa entrega de identidade. Tarefas 7 e 8 geram commits separados de infraestrutura e documentação, para revisão independente. Não misturar o conteúdo dos seis PRs por merge cego. Conservar a referência de origem de cada delta no commit e no relatório.

### Tarefa 1 — confirmação segura por código

**Arquivos existentes:** `backend/src/Duora.Infrastructure/Identity/AccountService.cs`, `backend/src/Duora.Infrastructure/Email/AccountEmailOutbox.cs`, `backend/src/Duora.Core/Notifications/AccountEmailCatalog.cs`, `AccountEmails.cs` no mesmo diretório; `backend/src/Duora.Web/Accounts/AuthEndpoints.cs`; testes `Identity/ConfirmationAbuseTests.cs`, `Identity/EnumerationTimingTests.cs` e `Notifications/AccountEmailsTests.cs` nos respectivos projetos.

**Criar:** `backend/tests/Duora.IntegrationTests/Identity/EmailConfirmationCodeTests.cs`, aproveitando a versão do PR 94; `backend/src/Duora.Infrastructure/Identity/EmailConfirmationProof.cs`, separando formato, emissão e expiração da prova.

**Interfaces:** preservar `ConfirmEmailRequest(Guid? UserId, string? Token, string? Password)` durante compatibilidade; `Token` aceita código ou prova legada exclusivamente quando não há prova nova. O armazenamento da prova inclui versão, security stamp, hash, `IssuedAt` e `ExpiresAt`; reenvio invalida a prova anterior atomicamente.

- [ ] Extrair apenas os hunks de e-mail do PR 94, começando pelos testes, sem aplicar seu ADR ou fluxo etário. Inspecionar `git diff origin/main...origin/feat/email-code-selfie -- <arquivo>` antes de cada aplicação.
- [ ] Executar os testes novos na base e confirmar falha pela ausência de código numérico. Acrescentar a fronteira exata de expiração e o consumo concorrente:

```csharp
Assert.Matches("^[0-9]{8}$", proof.Token);
var replies = await Task.WhenAll(
    first.PostJsonAsync("/api/v1/auth/confirm-email", body),
    second.PostJsonAsync("/api/v1/auth/confirm-email", body));
Assert.Single(replies, response => response.StatusCode == HttpStatusCode.NoContent);
Assert.Single(replies, response => response.StatusCode == HttpStatusCode.BadRequest);
```

Usar `IdentityScenario`, `Browser`, `FakeTimeProvider` e `PostgresFixture` existentes; o corpo contém `userId`, `token` e `password`. Confirmar também senha incorreta, cadastro substituído, limite de falhas, prova antiga após reenvio e links antigos durante a janela de compatibilidade.

- [ ] Implementar geração e proteção, mantendo custo equivalente e respostas neutras:

```csharp
var code = RandomNumberGenerator.GetInt32(100_000_000)
    .ToString("D8", CultureInfo.InvariantCulture);
var codeHash = _users.PasswordHasher.HashPassword(user, code);
var issuedAt = _time.GetUtcNow();
var expiresAt = issuedAt + TimeSpan.FromMinutes(15);
```

Reusar o lock/reload de conta do 94; verificar stamp, senha e prazo dentro da transação. Consumir prova, atualizar confirmação e enfileirar efeito na mesma transação. Não usar CreatedAt para expiração de reenvio. Conservar limite de cinco falhas já configurado, acrescentar cooldown de 60 segundos e teto proposto de cinco emissões por hora por conta/IP, persistidos; confirmar esses parâmetros no relatório como escolhas operacionais testadas, não decisão prévia da issue.

- [ ] Rodar `dotnet test --solution backend/Duora.slnx` com PostgreSQL real. Não reduzir testes de enumeração para acomodar regressão.
- [ ] Commit isolado `feat(identity): confirm email with single-use expiring codes`.

### Tarefa 2 — política de selfie independente de idade

**Criar:** `backend/src/Duora.Core/Identity/SelfieVerification.cs`, `ISelfieProvider.cs`, `ParticipationEligibility.cs`; testes `backend/tests/Duora.UnitTests/Identity/SelfieVerificationTests.cs` e `ParticipationEligibilityTests.cs`.

**Interfaces novas:**

```csharp
public enum SelfieState { Pending, Capturing, Processing, Passed, RecaptureRequired, Inconclusive, Expired }
public sealed record SelfieSession(string Reference, DateTimeOffset ExpiresAt);
public sealed record SelfieResult(SelfieState State, string? FeedbackCode, string PolicyVersion);
public interface ISelfieProvider
{
    Task<SelfieSession> StartAsync(Guid attemptId, CancellationToken cancellationToken);
    Task<SelfieResult> ReadAsync(string reference, CancellationToken cancellationToken);
}
public static class ParticipationEligibility
{
    public static bool CanParticipate(AccountStatus status, bool emailConfirmed, SelfieState? selfie)
        => status == AccountStatus.Active && emailConfirmed && selfie == SelfieState.Passed;
}
```

- [ ] Testar a matriz inteira de AccountStatus, e-mail confirmado/não confirmado e cada estado de selfie. Exemplo do comportamento essencial:

```csharp
Assert.True(ParticipationEligibility.CanParticipate(AccountStatus.Active, true, SelfieState.Passed));
Assert.False(ParticipationEligibility.CanParticipate(AccountStatus.Active, false, SelfieState.Passed));
Assert.False(ParticipationEligibility.CanParticipate(AccountStatus.Restricted, true, SelfieState.Passed));
Assert.False(ParticipationEligibility.CanParticipate(AccountStatus.Active, true, null));
```

- [ ] Confirmar falha de compilação antes de criar os tipos; implementar política pura sem idade, relógio externo, HTTP ou SDK.
- [ ] Testar máquina de estados: somente tentativa atual aceita resultado; Passed exige resultado autenticado; quality failure conduz a RecaptureRequired; indisponibilidade e timeout nunca ativam; estado terminal não volta a Pending.
- [ ] Executar unidade/arquitetura e commit `feat(identity): define age-independent selfie eligibility`.

### Tarefa 3 — persistência, autorização e desativação do legado

**Criar:** `backend/src/Duora.Infrastructure/Identity/SelfieVerificationRecord.cs`, `SelfieVerificationService.cs`, `SelfieVerificationServiceCollectionExtensions.cs`; migration gerada `SelfieVerifications`, SQL revisável; testes `backend/tests/Duora.IntegrationTests/Identity/SelfieMigrationTests.cs`, `SelfieAuthorizationTests.cs`.

**Modificar:** `backend/src/Duora.Infrastructure/Persistence/DuoraDbContext.cs`, snapshot gerado; `backend/src/Duora.Web/Security/ParticipationPolicies.cs`, `DuoraWebApp.cs`, `Accounts/AgeVerificationEndpoints.cs`, `Webhooks/WebhookEndpoints.cs`; `backend/src/Duora.Worker/WorkerHost.cs`.

**Interfaces:** `SelfieVerificationService.GetEligibilityAsync(Guid, CancellationToken)` retorna `Task<bool>`; `RequestAsync(Guid, CancellationToken)` cria/retorna tentativa do titular; `GetCurrentAsync(Guid, CancellationToken)` retorna estado mínimo. A policy consulta o serviço sem cache e mantém tratamento de falha de banco como 503.

- [ ] Escrever testes HTTP pela rota sentinela existente: e-mail+selfie Passed+Active permite; VerifiedAdult sem selfie não permite; ausência de idade não impede conta com selfie válida; MFA/sanção/sessão continuam valendo.
- [ ] Adicionar tabela `identity.selfie_verifications`: id, user_id, state, provider_reference, policy_version, requested_at, expires_at, completed_at, nonce e versão de concorrência. Uma tentativa aberta por conta com índice único parcial; sem coluna de imagem ou idade.
- [ ] Gerar migration expansiva com dotnet-ef, revisar SQL e testar em banco descartável migrado da base. Não editar migrations históricas nem executar Down destrutivo.
- [ ] Desregistrar consumidor e fornecedor etários dos hosts. Rotas antigas autenticadas retornam 410 sem efeito; callback etário não altera conta. Atualizar testes antigos para verificar descontinuação, mantendo cobertura de autenticação, replay e sanções no fluxo novo.
- [ ] Migrar contas pendentes para significado de pendência de selfie sem reutilizar a evidência etária. Para contas ativas, autorização já exige selfie nova. Preservar todas as sanções independentes. Produzir relatório de motivos ambíguos para revisão, sem alteração permissiva automática.
- [ ] Testar callback legado com NotAdult/VerifiedAdult após migração: status e sessão permanecem intactos; testar reexecução da migração/compensação sem duplicação.
- [ ] Atualizar fixtures de participação para criarem explicitamente selfie válida, sem simplesmente marcar todas as contas como elegíveis. Rodar integração completa e commit.

### Tarefa 4 — fornecedor AWS, filas e retenção

**Criar:** `backend/src/Duora.Infrastructure/Identity/RekognitionSelfieProvider.cs`, `SelfieProviderOptions.cs`, `SelfieVerificationDispatcher.cs`, `SelfieVerificationOutbox.cs`; `backend/src/Duora.Worker/SelfieVerificationProviderService.cs`; testes `backend/tests/Duora.IntegrationTests/Identity/SelfieDispatchTests.cs` e `backend/tests/Duora.UnitTests/Identity/RekognitionSelfieProviderTests.cs`.

**Consome:** ISelfieProvider da tarefa 2 e tentativa persistida da tarefa 3. **Produz:** sessão de captura e resultado mínimo, autenticado exclusivamente no servidor.

- [ ] Verificar documentação oficial e versões/licenças de AWSSDK.Rekognition e componente Amplify antes de instalar; atualizar lockfiles e inventário de licenças. Não usar versões adivinhadas nem permissões FullAccess dos exemplos.
- [ ] Criar testes de transporte para exigir a configuração de minimização:

```csharp
var attributes = new[] { "DEFAULT" };
Assert.DoesNotContain("ALL", attributes);
Assert.DoesNotContain("AGE_RANGE", attributes);
```

O teste real intercepta a requisição enviada pelo adaptador e aplica essas asserções ao payload capturado, não a constante isolada. Testar ausência de OutputConfig, AuditImagesLimit=0, conta errada, nonce errado, resposta desconhecida, cancelamento e timeout.

- [ ] Implementar Start com ClientRequestToken estável derivado de attemptId; Read consulta sessão e avalia qualidade do frame em memória. Solicitar somente DEFAULT. Mapear nitidez, iluminação e pose com política versionada e validada na inicialização. Sem limiar configurado, recusar ativação do adaptador; nunca usar zero como aprovação padrão.
- [ ] Adaptar do commit `5ac71026` do PR 88 o teto de tentativas, heartbeat e fencing para o dispatcher de selfie. Sem chamadas externas dentro de locks de negócio. Registrar tarefa de consulta durável com deadline anterior à expiração da sessão, backoff limitado e idempotência. Callback da SPA só antecipa a consulta, nunca aceita score.
- [ ] Testar dois workers, pausa além do lease, efeito externo antes de ACK, timeout depois de criar sessão, expiração sem resultado e suspensão concorrente. Em cada caso, contar chamadas externas e transições, não apenas ausência de exceção.
- [ ] Aplicar remoção de metadados transitórios após 24h e limpeza por exclusão de conta. Não manter frame em banco/log/outbox; desabilitar captura de corpo em observabilidade para esse fluxo. Aprovação conserva somente estado, versão de política e data enquanto a conta existir.
- [ ] Documentar opt-out AWS e retenção contratual como gates de ativação; não tratar expiração da API como prova de eliminação no fornecedor. Sandbox/câmera reais não serão alegados sem execução autorizada e credenciais de teste.
- [ ] Rodar testes do adaptador e filas com PostgreSQL; commit separado.

### Tarefa 5 — API, SPA e acessibilidade

**Criar:** `backend/src/Duora.Contracts/Identity/SelfieContracts.cs`, `backend/src/Duora.Web/Accounts/SelfieVerificationEndpoints.cs`; `frontend/src/features/account/SelfieVerificationPage.tsx`, `selfie-verification-api.ts`, `SelfieVerificationPage.test.tsx`; `frontend/e2e/selfie.spec.ts`.

**Modificar:** `frontend/src/features/auth/ConfirmEmailPage.tsx`, `CheckEmailPage.tsx`, testes correspondentes; `frontend/src/app/AppRoutes.tsx`, `frontend/src/features/account/AccountPage.tsx`, `frontend/src/shared/paths.ts`, catálogos `catalog.pt-BR.ts` e `catalog.en-US.ts`; OpenAPI e `schema.d.ts` gerados.

**Contrato:** POST `/api/v1/me/selfie-verifications` sem resultado no corpo; GET `/api/v1/me/selfie-verifications/current`; POST `/api/v1/me/selfie-verifications/{id}/complete` sinaliza consulta. Resposta mínima:

```csharp
public sealed record SelfieVerificationResponse(
    string State, string NextStep, string? FeedbackCode, DateTime? ExpiresAt);
```

Credenciais temporárias de captura são emitidas em endpoint próprio, autenticado, limitado e no-store; IAM autoriza somente operação necessária. Não expor credenciais permanentes, resultado bruto ou frame. SessionId deve estar vinculado à tentativa do titular, sem aceitar referência arbitrária no complete.

- [ ] Testar CSRF, conta alheia, corpo com `isAdult`, `age` ou `score`, expiração e limite de solicitações; exigir rejeição de campos desconhecidos e nenhuma consulta ao fornecedor em ID alheio.
- [ ] Adaptar somente a tela de código do 94. Campo texto com inputMode numeric e autoComplete one-time-code; manter zeros iniciais, aceitar colagem de oito dígitos, não colocar código em URL/telemetria e manter senha.
- [ ] Integrar FaceLivenessDetector oficial com obtenção de credenciais temporárias. Exibir feedback traduzido por código conhecido; não mostrar mensagens brutas do fornecedor. Encerrar stream ao sair/cancelar e descartar dados ao trocar conta.
- [ ] Testar instruções, foco, teclado, aria-live, iluminação insuficiente, câmera negada/ausente, sessão expirada e cancelamento. Os mocks verificam comportamento local; câmera/VoiceOver reais são validação separada. Não criar fallback de documento ou upload de foto.
- [ ] Rodar `dotnet build backend/Duora.slnx`, `corepack pnpm --dir frontend api:generate`; revisar diff OpenAPI e migração do cliente como quebra explícita.
- [ ] Rodar typecheck/lint/format/test/build/e2e completos; commit.

### Tarefa 6 — documentação e auditoria de remoção etária

**Modificar:** `docs/product/spec.md`, `scope.md`, `docs/architecture/modules.md`, `docs/security/auth.md`, `docs/privacy/data-map.md`, `docs/design/screens.md`, `docs/testing/acceptance.md`, índice de ADRs e adendo do 0024. **Criar:** ADR 0029 na base atual, com título de e-mail e presença sem idade; renumerar se outra decisão ocupar o número antes da publicação.

- [ ] Atualizar INV-01/T-01 e API-A02/A03/A06/P05/P06; separar idade declarada do perfil de verificação etária removida. Não apagar história de ADRs ou migrations.
- [ ] Buscar resíduos com `rg -n 'VerifiedAdult|NotAdult|MinorSuspected|AgeEligibility|age-verifications' backend/src frontend/src docs`. Classificar cada ocorrência: histórico/migration, descontinuação/teste negativo ou resíduo a remover. Nenhuma ocorrência operacional pode decidir participação por idade.
- [ ] Documentar plano de comunicação a contas legadas, rollout coordenado, desativação do worker antigo, rollback sem reativação de idade e gates de fornecedor/acessibilidade.
- [ ] Executar backend completo com Docker e frontend completo; verificar formato e contrato. Relatório discrimina teste real, mock e não executado. Commit documental acompanha a entrega de identidade.

### Tarefa 7 — infraestrutura e observação

**Arquivos:** os novos diretórios `infra/telemetry/`, `scripts/telemetry/`, `docs/operations/telemetry.md` do commit `71c98f2e`; ajustar `infra/README.md`; `infra/compose/compose.prod.yml`, `scripts/deploy/test_capacity_budget.py`, `docs/operations/capacity.md` do delta `1788c843`.

- [ ] Aplicar deltas por arquivo sobre a main atual, sem trazer commits ancestrais de documentação do 87. Ler o AGENTS.md próprio do observador como instrução do componente, sem conceder acesso à produção.
- [ ] Acrescentar testes de CPU/RAM por cenário antes de editar limites: postgres 2048 MiB; permanentes 5248 MiB; tetos web/postgres 1,5 CPU; worker 1; edge/backup 0,5. Pesos postgres/web 2048, edge 2048, worker 768, backup 512; migrador/restore fora do cenário normal. Monitor/gateway só podem ativar quando incluídos no cenário agregado e cabendo na margem real.
- [ ] Validar mutações negativas em cópia temporária: memória acima do orçamento, serviço não contabilizado, CPU acima do host, privilégio ou socket Docker no observador. Repor arquivo após cada teste; nenhuma mudança no checkout original.
- [ ] Rodar `python3 -m unittest discover -s scripts/telemetry -p 'test_*.py' -v`, `node --test scripts/telemetry/test-mcp.mjs`, suíte deploy em ambiente Python com requirements travado e Docker Compose config com valores sintéticos.
- [ ] Não executar install/firewall/activate na máquina ou VPS, nem enviar Slack. Registrar carga/VPS/backup como pendentes, não aprovados. Commit independente de infraestrutura.

### Tarefa 8 — política linguística e fechamento

**Arquivos:** `docs/product/spec.md`, `docs/architecture/adr/0026-publico-idiomas-e-regiao-do-servidor.md`; testes de locale/outbox existentes somente se houver lacuna demonstrável.

- [ ] Aplicar os dois deltas documentais do PR 92, preservando as alterações de identidade na spec. Cobrir idioma comum, opt-in bilateral entre locales, idioma combinado visível, ausência de tradução, não relaxar por espera e voz opcional sem penalidade.
- [ ] Comparar PR 91 com main: não reverter fallback en-US ou migração de preferência `en`; não duplicar testes já existentes. Rodar testes de locale e e-mail para registrar preservação.
- [ ] Commit separado; revisão global de escopo, segurança, migrações, contrato e acessibilidade. Não declarar o algoritmo de pareamento linguístico implementado por essa mudança documental.
- [ ] Conferir git diff/check, branch remota atual e todos os resultados; preparar relatório com arquivos alterados, testes aprovados/falhos/não executados, limitações, riscos e ausência/presença de instruções não confiáveis. Revisão humana obrigatória antes de merge/deploy.

## Aceite da execução

Implementação só é concluída quando todos os deltas aproveitáveis tiverem decisão rastreável, nenhum bloqueio etário permanecer operacional, os testes da nova regra passarem e as limitações de validação real estiverem explícitas. Código de integração pronto com mocks não equivale a fornecedor homologado. Qualquer gate não executado permanece pendente; não alterar scanners ou políticas para obter verde.

## Autorrevisão do plano

- Cobertura: tarefas 1–6 identidade/legado/fornecedor/contratos/SPA/documentação; 7 infraestrutura; 8 linguagem e entrega.
- Cinco condições de Review Focus têm testes atribuídos.
- Assinaturas de ISelfieProvider e GetEligibility são únicas e usadas pelas tarefas dependentes.
- Sem autorização de contratação, dados reais, merge ou implantação.
- Próxima etapa: revisão deste plano e escolha de execução direta ou por subagentes.

## Registro de execução — 19/10/2026

Executado diretamente na branch local maintenance/consolidation-93, base main 92150065. Infraestrutura em ea7a6c6c; identidade/SPA/contratos/documentação no commit subsequente. A main original não foi alterada, nenhum merge ou deploy foi feito.

Validações finais: 2454 testes backend com PostgreSQL em Docker, 670 Vitest, 132 Playwright, 358 testes de operações (os 14 dependentes de ambiente foram cobertos em execuções adicionais: 13 PostgreSQL + sonda Caddy), 10+7 gateway. Restore/install travados, tipos, lint, formato, build Debug, Compose config, Gitleaks e Trivy passaram. Revisão independente identificou e teve corrigidos dois defeitos da SPA; regressões incluídas.

Rulings: limite de e-mail mantido em três emissões/hora (mais restritivo que cinco) e persistido no banco; substituição de cadastro preserva a defesa contra pré-registro. Teto/cooldown selfie de cinco/hora e um minuto; solicitação aberta reutilizada. E-mail numérico tem entrega em até dez minutos, sem reduzir a janela histórica de links legados. Limite de selfie/indisponibilidade responde 503 nesta versão, sem criar tentativa. Testes etários ativos foram substituídos por testes de aposentadoria; harnesses históricos não registram serviços no produto.

Pendente antes de ativação: CSP/Permissions-Policy/assets do SDK, câmera e AWS reais, calibração, acessibilidade assistida/VoiceOver, contrato/opt-out/retencão do fornecedor, Release/ImageSharp e supply-chain completo de imagens/SBOM, comparador externo de contrato e revisão humana. Sem afirmar conclusão desses gates nem execução integral da matriz real de falhas de rede/fornecedor. O ADR-0029 concentra o plano de transição e rollback. Relatório detalhado entregue em outputs/validacao-duora.md da conversa.
