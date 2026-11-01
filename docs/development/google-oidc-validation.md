# Google OIDC — candidato local e evidências

Base: `6c397a34a19969e93148b7e5cdb760c508bbdec2`. Branch isolada `feat/google-oidc-276`. Política aprovada: senha/código Duora mantidos, associação explícita por posse, participante apenas, Google desligado por padrão. Nenhuma chave real, provedor real, PR, issue, push, merge ou produção configurados.

## Evidências executadas até o primeiro candidato

- `dotnet restore backend/Duora.slnx --locked-mode --disable-parallel`: passou na base. Restore posterior com dependência oficial OpenIdConnect 10.0.13/MIT atualizou os quatro lockfiles consumidores; repetição locked final está pendente.
- Builds de `Duora.IntegrationTests.csproj` com `--no-restore -m:1 /p:UseSharedCompilation=false`: passaram. Permanecem os dois avisos históricos de licença ImageSharp em Debug; nenhum erro de compilação.
- RED inicial PostgreSQL real: 1 falha esperada por rota Google ausente. O provedor simulado foi corrigido de PostConfigure para Configure, antes da criação do ConfigurationManager; a primeira execução com esse erro de harness não comprova validação negativa de tokens e foi substituída pelas execuções isoladas abaixo. O erro carregou discovery/JWKS públicos; não havia credenciais reais nem ativação de login real.
- Handler real + PostgreSQL 18.6 + discovery/JWKS/token endpoint sintéticos: 12/12 básicos passaram. Em seguida, 27 casos produziram 26 passes e um RED de formato temporal (+00:00 em vez de Z). O servidor foi corrigido para UTC Z.
- Callback duplicado/implicit: quatro REDs registrados; filtro exato antes do handler corrigiu o transporte. Exposição de e-mail de intenção anônima após login local: RED registrado; invalidação no login/logout, descarte do cookie e conferência do SID corrigiram o comportamento.
- `dotnet .../Duora.IntegrationTests.dll -class 'Duora.IntegrationTests.Identity.GoogleOidc*' -class Duora.IntegrationTests.Identity.EndpointAuthorizationTests -class Duora.IntegrationTests.Identity.CsrfEveryRouteTests -class Duora.IntegrationTests.Database.AppRolePrivilegesTests -parallel none`: **79/79**, zero erros/falhas/skips. Inclui 32 casos Google, MFA antes de qualquer sessão, troca de SID/conta, replay, corrida real com barreira, rollback de aviso/credencial e operação de limpeza independente de flags.
- Verificação adicional do consumidor registrado do Worker: passou, apagando prova vencida com Google off, sem protetor Web e sem novos requests. Dois novos testes detectaram caminhos de signout herdados do handler respondendo 200; foram desativados por `PathString.Empty`. O GREEN desses dois casos ainda não foi executado no candidato inicial, por encerramento da janela exclusiva de testes.
- Frontend RED: 10 falhas por funcionalidade ausente, 1 comportamento anterior passou. GREEN final: 12 casos Google; suíte completa **1.173/1.173**, 96 arquivos; regressões finais **42/42**. Corrigido o cache de flags para manter a exigência antiga de cache vazio após logout, sem reduzir testes. Resposta tardia de flow após mudança de titular é descartada em UI/cache.
- `pnpm typecheck`, `pnpm lint`, `pnpm format:check`, `pnpm build`: passaram. Aviso histórico de tamanho do chunk SelfieCapture permanece.
- Google Playwright: **6/6**, produção preview, Chromium/WebKit/mobile e PT-BR/EN-US. Prova navegação inteira e gesto explícito; API/retorno sintéticos não equivalem ao protocolo OIDC, que é exercitado no backend.
- `pnpm --dir frontend api:generate`: passou após OpenAPI gerado pelo build. `git diff --check`: passou.

## Revisão incremental após análise independente

A revisão do SHA `112add751b77135e1f1d0f3a9e83b17f9b23d09c` encontrou uma falha de recuperação: flags de novos vínculos escondiam a gestão, a conta suspensa não alcançava a lista/tela e a remoção reutilizava uma guarda que negava papéis privilegiados. Registrei RED real: backend **6 falhas esperadas / 8 casos** (2 guardas já passavam) e frontend **4 falhas / 4 casos**, sem erros de importação/execução.

Correção: a remoção agora usa guarda local separada; mantém senha utilizável/recente, SID/stamp, lockout e TOTP quando ativo, sem negar o titular por mudança de papel. GET/DELETE da gestão continuam acessíveis ao suspenso e com provider/linking off; novos vínculos/login privilegiados continuam negados. UI de recuperação não depende das flags e aparece também na suspensão. Não há rede nem credencial Google nessa operação. A lista explícita de rotas de segurança da suspensão ganhou apenas GET da lista e DELETE; os demais bloqueios permanecem.

Verificações do delta, em janela exclusiva e sem paralelismo pesado:

- Restore locked final: `dotnet restore backend/Duora.slnx --locked-mode --disable-parallel` passou.
- Build serial de integração: passou, 0 erros e os 2 avisos históricos ImageSharp.
- `dotnet .../Duora.IntegrationTests.dll -class 'Duora.IntegrationTests.Identity.GoogleOidc*' -class Duora.IntegrationTests.Safety.SuspendedAccountRoutesTests -class Duora.IntegrationTests.Identity.EndpointAuthorizationTests -class Duora.IntegrationTests.Identity.CsrfEveryRouteTests -class Duora.IntegrationTests.Database.AppRolePrivilegesTests -parallel none`: **92/92**, zero erros/falhas/skips. Inclui os 2 signout, consumidor Worker desligado e as regressões de recuperação.
- Frontend do delta: **58/58** em GoogleAuth, GoogleRecovery, AccountPage, SuspendedAccountPage e session-hooks; typecheck/lint/format passaram. A suíte completa de 1.173 e os 6 Playwright citados antes pertencem ao candidato anterior; não foram repetidos no delta.
- `dotnet format backend/Duora.slnx --verify-no-changes --no-restore --include <35 arquivos C# da tarefa>` passou após corrigir somente a ordem de imports em 2 arquivos. Código gerado de migração/snapshot fica fora dessa verificação focada por convenção; format integral continua gate central.
- `pnpm --dir frontend api:generate` passou após o OpenAPI atualizado; `git diff --check` passou.

## Verificações restantes

A janela foi liberada explicitamente para a outra frente, sem processos ativos. A integração central executará suíte completa de backend/unidade/arquitetura/integração, frontend/Playwright preexistentes conforme necessário, format integral, secret-scan, contract e supply-chain, além da revisão do delta e revisão humana. Os gates permanecem exigidos. Não se afirma que CI, integração, merge ou ativação já foram aprovados.

## Limites e riscos operacionais

- A issue original descrevia funcionalidade ausente; não foi demonstrada vulnerabilidade Google/OIDC no runtime publicado. Defeitos relatados aqui pertenciam ao candidato em desenvolvimento e foram capturados antes de integração.
- Cadastro precisa terminar prova Google em cinco minutos, mesmo que o código de e-mail dure quinze; intenção vencida pode ser concluída localmente e vinculada depois.
- Revogação no Google não encerra tickets Duora automaticamente. Reset de senha mantém MFA e vínculo, com aviso e orientação para revisar/remover métodos em caso de invasão.
- Flags desligadas retiram novos desafios/cadastro/vínculo; gestão e remoção locais permanecem disponíveis com prova do titular, mesmo durante rollback e suspensão. Não há revogação Google em tempo real.
- Publicação de privacidade, console/credenciais de homologação, callback/proxy com query redigida, navegadores com fornecedor real e reconciliação de exclusão/restore/hub continuam gates próprios. Não se presume prova operacional a partir do stub.
- Sem instrução não confiável detectada. Nenhum keyring, segredo real ou volume alheio foi exportado; Testcontainers próprios foram removidos, sem prune.
