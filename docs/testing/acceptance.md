# Matriz de testes de aceite

Matriz T-01 a T-28 da spec 20.2, com invariantes ([scope.md](../product/scope.md)), rotas da API (spec 8),
camada de teste prevista (spec 20.1) e marco (spec 22). O aceite é orientado por risco, não por percentual de
cobertura (spec 20.3).

## Como ler

- **Marco:** os testes listados para o M1 vêm da tabela da spec 22. Os demais marcos foram atribuídos pela
  dependência funcional (o marco em que a funcionalidade nasce) e estão marcados como *inferido*.
- **Camadas:** Unidade (xUnit v3, `TimeProvider` e semente injetados), Integração (Testcontainers +
  PostgreSQL 18 real, conexões concorrentes com barreiras), HTTP (`WebApplicationFactory` com clientes e
  cookies distintos), Frontend (Vitest, Testing Library, MSW), E2E (Playwright com contextos A, B e C),
  Carga (k6 e harness .NET SignalR), Resiliência (falhas injetadas), Recuperação (restore e VPS limpa),
  Processo (ensaio de permissões e revisão de configuração).
- **Status:** `não implementado` → `parcial` (cobre parte do cenário; diga qual) → `passando` (com link para a
  evidência do runner). Nenhum status muda sem artefato do CI ou relatório de ensaio (spec 25.19).

## Matriz

| ID | Cenário | Resultado obrigatório | Invariante / API | Camada prevista | Marco | Status |
|---|---|---|---|---|---|---|
| T-01 | Conta pendente, suspensa ou sem requisito etário tenta participar | API nega, hub não concede contexto e inscrição não avança | INV-01; API-E03, API-E05, API-R01, hub | HTTP, Integração | M1 | não implementado |
| T-02 | Dois coordenadores tentam reservar a mesma pessoa | Uma reserva; conflito tratado sem participante duplicado | INV-02; `BuildRoundPairings` | Integração (concorrência) | M2 (inferido) | não implementado |
| T-03 | Bloqueio disputa com mensagem, pareamento e leitura | Operações ordenadas pelo commit; novas autorizações negadas após o bloqueio | INV-03; API-S01, API-C03, API-C04, API-P04, `BuildRoundPairings` | Integração (concorrência), HTTP | M1 (¹) | não implementado |
| T-04 | Duas decisões positivas simultâneas e seus retries | Uma conexão e uma conversa; escolhas privadas preservadas | INV-04, INV-06; API-R06 | Integração (concorrência) | M2 (inferido) | não implementado |
| T-05 | Participante ou terceiro tenta ler pista, decisão ou campo alheio | Payload não contém informação proibida, inclusive em erro e notificação | INV-05; API-R02, API-R05, API-P04, hub | HTTP, E2E (contexto C) | M2 (inferido) | não implementado |
| T-06 | Mesmo comando é reenviado e a chave é reutilizada com outro corpo | Efeito único; resultado seguro repetível; payload divergente rejeitado (`409 IDEMPOTENCY_KEY_REUSED`) | INV-06; `Idempotency-Key`, API-E03, API-R03, API-C04 | Integração, HTTP | M2 (inferido) | não implementado |
| T-07 | Evento SignalR perde-se, duplica ou chega fora de ordem | Snapshot converge ao estado correto sem duplicar efeito | INV-07; hub `/hubs/updates`, API-R02, API-C03 | Frontend, E2E, harness SignalR | M2 (inferido) | não implementado |
| T-08 | Web ou worker reinicia antes/depois do commit, envio ou ACK | Job retoma com fencing; prazos e reservas consistentes | INV-08; outbox e jobs | Resiliência, Integração | M2 (inferido) | não implementado |
| T-09 | Catálogo recebe versão nova durante a partida | Partida usa a versão antiga imutável até terminar | INV-09; API-ADM09, API-R03 | Integração | M2 (inferido) | não implementado |
| T-10 | Moderador lê caso, aplica medida e outro revisa recurso | Acesso mínimo e auditoria completa de autor, política e resultado | INV-10; API-ADM11 a API-ADM15, API-S06 | HTTP, Integração | M1 (²) | não implementado |
| T-11 | Restore anterior a exclusão/revogação | Tombstones aplicados, sessões invalidadas e efeitos externos reconciliados | INV-11; procedimento de restore (spec 18.4) | Recuperação | M3 (inferido) | não implementado |
| T-12 | Conteúdo de issue tenta induzir agente a revelar segredo ou fazer deploy | Ambiente e permissões bloqueiam a ação; tentativa fica rastreável | INV-12; [permissions.md](../automation/permissions.md) | Processo | M0 (inferido) (³) | não implementado |
| T-13 | Login, logout ou escrita sem CSRF ou com origem indevida | Rejeição; endpoint público não significa mutação sem proteção | API-A01 a API-A05 e toda mutação cookie | HTTP | M1 | não implementado |
| T-14 | Cookie expira ou sessão é revogada com hub ativo | Novas ações negadas, conexão encerrada e cache privado limpo | API-A05, API-A10, hub | HTTP, E2E | M1 | não implementado |
| T-15 | Container recriado e chave de Data Protection rotacionada | Sessões legítimas seguem a política; a chave não some por efemeridade | Data Protection (spec 11.2) | Integração (container), Recuperação | M1 | não implementado |
| T-16 | Upload inválido, enorme, animado ou com metadados de localização | Rejeição ou normalização segura e consumo limitado | API-M01, API-M02 | Integração, Unidade | M1 | não implementado |
| T-17 | Provedor responde lentamente, falha ou repete callback | Timeout, dedupe, fila segura e nenhuma liberação indevida | INV-01, INV-06; `/webhooks/{provider}`, API-P05 | Resiliência, Integração | M1 | não implementado |
| T-18 | Relógio avança além do prazo ou usuário volta após longa queda | Sala expira corretamente; a rodada não se estende pelo relógio do cliente | INV-08; deadlines de sala e rodada | Unidade (`TimeProvider`), Integração | M2 (inferido) | não implementado |
| T-19 | Aba A salva perfil e aba B tenta sobrescrever versão antiga | `412` ou reconciliação; sem perda silenciosa da primeira alteração | API-P02, API-P03 (`If-Match`) | HTTP, Frontend | M1 (inferido) | não implementado |
| T-20 | Pessoa encerra conexão e uma ação antiga chega depois | Conexão não reabre; mensagem nova não é aceita | INV-03, INV-06; API-C02, API-C04, API-R06 | Integração, HTTP | M3 (inferido) | não implementado |
| T-21 | Identidade A faz logout e identidade B entra no mesmo navegador | Nenhum cache, foto, histórico ou socket de A aparece para B | API-A04, API-A05, hub | E2E, Frontend | M1 | não implementado |
| T-22 | Migração da versão anterior e rollback de aplicação | Nova release funciona; a antiga só volta com compatibilidade comprovada | Migrações (spec 17.4) | Integração (CI), Homologação | M0 (inferido) | não implementado |
| T-23 | Jornada com teclado, zoom, leitor de tela e movimento reduzido | Tarefa essencial concluída; A11Y-01 a A11Y-08 avaliados; saída e denúncia acessíveis | A11Y-01 a A11Y-08 | E2E (axe), avaliação manual | M3 | não implementado |
| T-24 | Carga de referência com imagens, jobs e perda parcial de rede | Metas medidas, limites respeitados e nenhuma invariante quebrada | Todas; metas da spec 19.1 | Carga | M3 | não implementado |
| T-25 | VPS inteira perdida e reconstruída das cópias externas | Serviço seguro recuperado; RPO/RTO medidos e lacunas registradas | INV-11; spec 18 | Recuperação | M3 | não implementado |
| T-26 | Asset apagado, reprovado ou perfil bloqueado acessado por URL antiga | Autorização atual nega; cache não reexpõe conteúdo | INV-03; API-M04, API-P04 | HTTP | M1 (inferido) | não implementado |
| T-27 | Replay de resposta idempotente depois de perder autorização | Autorização atual prevalece; não retorna payload privado antigo | INV-03, INV-06; `Idempotency-Key` | HTTP, Integração | M2 (inferido) | não implementado |
| T-28 | Lista de espera, cancelamento e check-in concorrem com lotação | Capacidade preservada e promoção de vaga determinística | API-E03, API-E04, API-E05 | Integração (concorrência) | M2 (inferido) | não implementado |

Notas:

1. A spec põe T-03 no M1, mas pareamento e mensagem só existem no M2. No M1 o teste cobre bloqueio contra
   leitura de perfil; os ramos de mensagem e pareamento entram no M2.
2. A spec põe T-10 no M1, mas a administração completa é entrega do M3. No M1 o teste cobre denúncia,
   decisão e recurso pelas rotas mínimas de moderação.
3. T-12 é um ensaio de processo: verifica que o agente não tem caminho até segredos ou deploy, não um teste
   automatizado de código. Deve passar antes de qualquer dado real (gate de automação, spec 23.3).

## Verificações derivadas de ADRs

Não são cenários T-NN da spec, mas entram no aceite do marco indicado.

| Origem | Verificação | Camada | Marco | Status |
|---|---|---|---|---|
| [ADR-0016](../architecture/adr/0016-sala-e-sessao-de-jogo.md) | Rotas `/rooms/{roomId}` resolvem a `game_session` pelo mesmo ID; decisão única por `(game_session_id, user_id)` (coberto junto com T-02, T-04, T-05 e T-09) | Integração, HTTP | M2 | não implementado |
| [ADR-0017](../architecture/adr/0017-mfa-por-papel.md) | Moderador/administrador sem MFA confirmado é negado em `/admin/*`; API-A15 não remove o único método de conta privilegiada; participante ativa e desativa TOTP | HTTP | M1 | não implementado |
| [ADR-0018](../architecture/adr/0018-contrato-openapi-code-first.md) | OpenAPI versionado igual ao gerado; `oasdiff breaking` sem aprovação falha; tipos do frontend sem diff após regenerar | CI | M0/M1 | não implementado |

## Evidência por release

Cada release registra, por teste: commit, ambiente, dataset sintético, resultado e link para o artefato do
runner (template de relatório de teste, spec 21.3). Testes de carga e recuperação têm relatório próprio em
`docs/testing/performance.md` e `docs/operations/backup-restore.md` (a escrever).
