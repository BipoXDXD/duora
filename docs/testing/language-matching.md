# Pareamento linguístico: validação e revisão

A política está na spec e no ADR-0026. Este documento descreve a execução da fatia implementada; o modelo alvo completo permanece em `docs/data/model.md`.

## Executar

A partir da raiz do repositório, com o SDK e o Node fixados pelo projeto e Docker disponível:

```sh
dotnet restore backend/Duora.slnx --locked-mode
dotnet build backend/Duora.slnx --no-restore
dotnet format backend/Duora.slnx --verify-no-changes --no-restore
dotnet test --solution backend/Duora.slnx --no-build
corepack pnpm --dir frontend api:generate
corepack pnpm --dir frontend typecheck
corepack pnpm --dir frontend lint
corepack pnpm --dir frontend format:check
corepack pnpm --dir frontend test --maxWorkers=2
corepack pnpm --dir frontend e2e
corepack pnpm --dir frontend e2e:real
```

O último comando constrói a SPA e inicia `Duora.BrowserHost`, um projeto exclusivo de teste. Ele usa PostgreSQL 18.6 descartável, HTTPS local na porta 5443, contas sintéticas e relógio controlável. Não reutiliza servidor ativo e não adiciona endpoints de teste à aplicação de produção. Não exige segredos nem conta de produção. O cenário real não intercepta respostas HTTP; percorre declarações, ausência de idioma comum, recusa por cada lado, aceite, conteúdo individual e conclusão sem microfone.

## Cobertura específica

- `LanguageMatchingTests`: legado sem declaração, interseção, quatro combinações de opt-in, espera, bloqueios e independência de critérios regionais opacos.
- Testes Profiles: preferências privadas/locale, versões condicionais, concorrência de criação/atualização e cadastro sem alteração da conta existente.
- `ParticipationGuardTests`: preferências e locale antes/depois da reserva, barreiras observáveis no PostgreSQL, rollback e eventos concorrentes sem reserva dupla.
- `ParticipationTests`: convites, recusas, prazo, pausa, lease/fencing e recuperação persistida; expiração do motor libera reservas mesmo após perda de elegibilidade.
- `ParticipationAuthorizationTests`: titular/terceiro, leitura e replay depois de bloqueio, negação de publicação a participante.
- `CatalogPublicationTests`: bilinguismo estrutural, publicação/MFA, bytes e hash de áudio, locale fixado, terceiro/bloqueio e imutabilidade após publicação/retirada.
- SPA: declaração sem escolhas implícitas, 412 com rascunho preservado, troca de conta durante leitura/salvamento do locale, avisos e fluxo sem microfone.

Não confundir os 129 testes de navegador da SPA com o cenário adicional que usa backend/PostgreSQL reais. Os testes de unidade das duas experiências validam localização, mas a fatia operacional implementada executa Constelação; o adaptador persistente de Rotas Improváveis continua pendente.

## Revisão humana e limites

Revisar a faixa completa de commits, porque os adaptadores e contratos têm dependências entre si. Os endpoints administrativos publicam conteúdo revisado; esta mudança não fornece mídia editorial real. HTTP/polling recupera estado; entrega push, chat e voz não são introduzidos. Região não é inferida de idioma e não recebe taxonomia ou detector novos.

A pausa operacional e o protocolo de locks/jobs estão em `docs/data/concurrency.md`. Não executar rollback de dados destrutivo nem reativar seleção que desconheça as declarações privadas. Os gates de imagens, SBOM, assinatura e Release continuam necessários antes de integração/produção; testes Debug e varredura de dependências do código não os substituem.
