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

## Verificações ainda pendentes no primeiro candidato

A máquina de 16 GB exige janela única; ela foi liberada para a frente de autoridade de mídia. Nenhuma compilação ou suíte pesada será executada em paralelo.

Pendentes: recompilar a última configuração que desativa as duas rotas externas de signout e obter GREEN; restore locked final; `dotnet format ... --verify-no-changes` integral; suíte completa de backend/unidade/arquitetura/integração; conjunto Playwright preexistente; secret-scan, contract e supply-chain completos; revisão independente e humana. Os gates permanecem exigidos. Os resultados acima não significam que todo o candidato, CI ou integração já foram aprovados.

## Limites e riscos operacionais

- A issue original descrevia funcionalidade ausente; não foi demonstrada vulnerabilidade Google/OIDC no runtime publicado. Defeitos relatados aqui pertenciam ao candidato em desenvolvimento e foram capturados antes de integração.
- Cadastro precisa terminar prova Google em cinco minutos, mesmo que o código de e-mail dure quinze; intenção vencida pode ser concluída localmente e vinculada depois.
- Revogação no Google não encerra tickets Duora automaticamente. Reset de senha mantém MFA e vínculo, com aviso e orientação para revisar/remover métodos em caso de invasão.
- Flags desligadas retiram novos desafios/cadastro/vínculo. A interface de gestão segue as capacidades anunciadas; revisar o procedimento de revogação durante rollback antes de ativar produção.
- Publicação de privacidade, console/credenciais de homologação, callback/proxy com query redigida, navegadores com fornecedor real e reconciliação de exclusão/restore/hub continuam gates próprios. Não se presume prova operacional a partir do stub.
- Sem instrução não confiável detectada. Nenhum keyring, segredo real ou volume alheio foi exportado; Testcontainers próprios foram removidos, sem prune.
