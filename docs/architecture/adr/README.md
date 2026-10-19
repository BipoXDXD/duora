# Architecture Decision Records

Decisões arquiteturalmente significativas do Duora: estrutura, características de qualidade, dependências,
interfaces e técnicas de construção. Os ADRs 0001 a 0015 transcrevem as decisões da tabela 23.1 da
[especificação](../../product/spec.md); a numeração de quatro dígitos corresponde aos IDs `ADR-001` a `ADR-015`
da spec. A partir do 0016, os ADRs resolvem lacunas encontradas na spec.

## Índice

| ADR | Decisão | Status | Revisar quando |
|---|---|---|---|
| [0001](0001-stack-backend-dotnet-postgresql.md) | C#/.NET 10 LTS, ASP.NET Core, EF Core/Npgsql e PostgreSQL 18 | Accepted | Política de suporte ou requisito demonstrado |
| [0002](0002-spa-react-typescript-build-estatico.md) | SPA React/TypeScript com build estático | Accepted | Requisito concreto de SSR/app nativo |
| [0003](0003-monolito-modular-web-worker.md) | Monólito modular com web e worker da mesma release | Accepted | Escala/domínio exigir autonomia real |
| [0004](0004-dbcontext-unico-schemas-logicos.md) | Um `DuoraDbContext` inicial e schemas lógicos | Accepted | Fronteiras maduras com solução de atomicidade |
| [0005](0005-identity-local-cookie-ticket-persistente.md) | Identity local, cookie e ticket persistente | Accepted | Carga operacional/requisito justificar OIDC gerenciado |
| [0006](0006-mesma-origem-spa-api-hub.md) | Mesma origem para SPA/API/hub | Accepted | Separação necessária de domínios |
| [0007](0007-signalr-transporta-invalidacao.md) | SignalR transporta invalidação, não verdade do jogo | Accepted | Perfil de tráfego exigir protocolo diferente |
| [0008](0008-postgresql-para-jobs-e-outbox.md) | PostgreSQL para jobs/outbox antes de broker | Accepted | Métricas demonstrarem gargalo ou fan-out |
| [0009](0009-uma-instancia-web-no-piloto.md) | Uma web ativa no piloto | Accepted | Disponibilidade/capacidade exigir backplane |
| [0010](0010-object-storage-externo-leitura-via-api.md) | Object storage externo e leitura privada via API | Accepted | Egress/latência exigirem distribuição revisada |
| [0011](0011-pgbackrest-wal-externo-restore-obrigatorio.md) | pgBackRest/WAL externo e restore obrigatório | Accepted | Volume/metas exigirem outra topologia |
| [0012](0012-pareamento-deterministico-sem-llm.md) | Pareamento determinístico sem LLM | Accepted | Evidência de benefício com avaliação formal de risco |
| [0013](0013-ia-fora-do-caminho-critico.md) | IA fora do caminho crítico de partidas | Accepted | Caso concreto aprovado com fallback equivalente |
| [0014](0014-automacao-com-gates.md) | Automação com gates para mudanças críticas | Accepted | Evidência para ampliar escopo de baixo risco |
| [0015](0015-sem-kubernetes-redis-microsservicos.md) | Sem Kubernetes/Redis/microsserviços obrigatórios | Accepted | Necessidade medida |
| [0016](0016-sala-e-sessao-de-jogo.md) | Sala é a `game_session` de um par numa rodada | Accepted | Sala sem sessão ou com várias sessões |
| [0017](0017-mfa-por-papel.md) | MFA obrigatório para administração e moderação, opcional para participantes | Accepted | Tomada de conta ou exigência regulatória |
| [0018](0018-contrato-openapi-code-first.md) | Contrato OpenAPI code-first, versionado, com `oasdiff` | Accepted | Consumidores externos da API |
| [0019](0019-implementacao-identidade-sessao-outbox.md) | Identidade, sessões revogáveis, CSRF, rate limit e outbox de e-mail no M1 | Accepted | Mais de uma instância web, provedor de e-mail real ou MFA |
| [0020](0020-persistencia-da-sessao-de-jogo.md) | Sessão de jogo persistida por snapshot público no Core, estado JSONB versionado e conteúdo pela versão fixada | Accepted | Estado consultado por SQL ou snapshot acima de 32 KiB |
| [0021](0021-jobs-duraveis-e-leases-no-postgresql.md) | Jobs duráveis em `ops.jobs` com lease, fencing token, retry e `Expired` | Accepted | Idade do job mais antigo acima do alvo ou terceiro uso do protocolo de fila |
| [0022](0022-i18n-intl-messageformat-catalogo-tipado.md) | Interface em PT-BR e EN com intl-messageformat e catálogo tipado próprio | Accepted | Terceiro idioma ou tradução por fornecedor externo |
| [0023](0023-topologia-do-backup-repository-host-e-object-lock.md) | Backup por repository host separado com mTLS, credenciais fora do postgres e bucket com versionamento + Object Lock | Accepted | pgBackRest restringir operações por certificado ou provedor sem Object Lock |
| [0024](0024-fluxo-de-verificacao-etaria.md) | Verificação etária: fornecedor só no worker via outbox, webhook HMAC inline e regra pura no Core | Accepted | Fornecedor contratado ou parecer sobre o ECA Digital |
| [0025](0025-pipeline-de-fotos-imagesharp-s3-outbox.md) | Fotos por corpo cru, quarentena no disco da VPS (FileSystem); S3 como alternativa, fila na outbox e sanitização com ImageSharp no Worker | Accepted | Receita acima de US$ 1 mi, revisão editorial, URL assinada ou `media-sanitizer` isolado |
| [0026](0026-publico-idiomas-e-regiao-do-servidor.md) | Público principal fora do Brasil, produto inteiro em PT-BR e EN-US e servidor em Boston (EUA) | Accepted | Países do lançamento fixados, terceiro idioma ou servidor/backup em outro país |
| [0027](0027-midia-das-experiencias-cenario-audio-e-3d.md) | Cenários e personagens, áudio pré-gerado mudo por padrão, mídia na `ExperienceVersion` e 3D offline por padrão e em tempo real por experiência | Accepted | 3D em tempo real ou mídia acima do orçamento medido, mudança de plano ou termos de ElevenLabs, Freesound ou Sonniss, ou terceiro idioma |
| [0028](0028-voz-ao-vivo-webrtc-relay-cloudflare-turn.md) | Voz ao vivo por WebRTC P2P com relay forçado na Cloudflare Realtime TURN; LiveKit Cloud como plano B | Accepted | Mute pelo servidor, sala com mais de 2 pessoas ou relay instável no Safari |

| [0029](0029-codigo-email-selfie-sem-documentos.md) | Código digitável de e-mail e jornada de selfie sem documentos; integração real pendente | Proposed | Fornecedor, fraude/atrito ou app móvel |

## Regras

- Um arquivo por decisão: `NNNN-titulo-em-kebab-case.md`, numeração sequencial.
- Status: `Proposed`, `Accepted` ou `Superseded by ADR-NNNN`. Um ADR aceito nunca é apagado nem reescrito:
  uma decisão nova o substitui, e os dois se referenciam.
- Substituição parcial: quando uma decisão nova substitui só parte de um ADR aceito, o antigo continua `Accepted` e
  ganha a linha `Substituído em parte` (o que foi substituído e por qual ADR), mais um adendo datado que aponta o
  novo; o novo lista as partes substituídas.
- Escreva ADR quando a decisão for cara de reverter ou quando alguém, daqui a um ano, puder perguntar "por que
  isso é assim?". Decisão local e barata não precisa de ADR.
- Mudança que contradiz um ADR aceito exige novo ADR no mesmo PR (spec 14.1: mudança arquitetural exige ADR).

## Template

Campos da spec 21.3 (ID, status, contexto, decisão, alternativas descartadas, consequências,
segurança/operação, gatilho de revisão) mais Compliance.

```markdown
# ADR-NNNN: Título curto da decisão

- **Status:** Proposed | Accepted (AAAA-MM-DD) | Superseded by ADR-NNNN
- **Substituído em parte:** (opcional) o que foi substituído, pelo ADR-NNNN (AAAA-MM-DD)
- **Origem:** seções da spec, issue ou incidente que motivou a decisão
- **Revisar quando:** gatilho objetivo de revisão

## Contexto

Forças em jogo, restrições e o problema a resolver.

Alternativas consideradas:

- **Alternativa A.** Por que foi descartada (prós e contras no contexto do Duora).
- **Alternativa B.** Idem.

## Decisão

"Vamos ..." em voz afirmativa. Porquê técnico e porquê de negócio.

## Consequências

- Positivas:
- Negativas:
- Trade-off:

## Segurança e operação

Efeito sobre superfícies de ataque, segredos, dados pessoais, backup, deploy e observabilidade.

## Compliance

Como a decisão é verificada: teste de arquitetura, gate de CI, teste de aceite (T-NN) ou revisão manual.
Itens ainda não implementados indicam o marco previsto.
```
