# Revisão independente de segurança do PR #85

Base revisada: `af9d73b010fd337022cc0aacd63874931c6b7316`, branch `infra/csp-audio-microphone`.
O PR permanece **draft**, dependente do **#84**, com **revisão humana obrigatória** antes de integrar.
Não houve deploy, merge, mudança de workflow ou acesso a dados/segredos de produção.

## Conclusão

As duas liberações são suficientes no nível dos cabeçalhos para os fluxos descritos nos ADRs do #84:
catálogo de áudio da própria origem, Web Audio por fetch/ArrayBuffer e voz por MediaStream. Não se justifica
adicionar `blob:`, `data:`, curingas, fontes inseguras ou domínios TURN à CSP. Isso não constitui homologação
funcional da voz: a implementação ainda não existe no frontend desta branch.

| Controle | Avaliação e limite |
|---|---|
| `media-src 'self'` | Autoriza URLs de áudio/vídeo/track da própria origem. Não controla o fetch da Web Audio. O draft CSP3 exclui `blob:` de `self`; compatibilidade efetiva deve ser medida nos motores suportados. |
| `connect-src 'self' wss://<host>` | Cobre fetch da mídia e das credenciais e a sinalização WebSocket. Mantido sem abertura adicional. Não controla destinos ICE/STUN/TURN. |
| `srcObject = MediaStream` | Não faz carga de uma URL por Fetch; não exige liberar um esquema `blob:`. Continua sujeito às regras de reprodução/autoplay do navegador. |
| WebRTC | `default-src 'none'` não desabilita conexões RTC. A diretiva `webrtc` do draft não deve ser tratada como controle interoperável de relay-only. O cliente e a sinalização precisam impor as regras do ADR-0028. |
| `microphone=(self)` | Permite solicitar captura na origem; não concede permissão, não limita por rota e não impõe gesto. Documentos da mesma origem podem herdar a permissão. Frames de outra origem não podem ampliá-la por `allow`. |
| Consentimento e gesto | `getUserMedia` requer contexto seguro e permissão. O gesto explícito é regra do produto, inclusive com permissão persistida. Autoplay e `AudioContext.resume()` são controles distintos; tratar rejeição e oferecer ação para retomar. |

## Problema comprovado e correção

**Cabeçalhos ausentes em erros da borda.** Antes da correção, os novos testes contra Caddy 2.11.7 falharam
em `/assets/missing.js` e `/missing`: status 404 sem CSP, Permissions-Policy ou HSTS e com `Server: Caddy`.
O asset inexistente ainda recebia `Cache-Control: public, max-age=31536000, immutable`.
Não foi demonstrado um exploit de execução de script: o corpo observado era vazio. O defeito é a quebra do
contrato de cabeçalhos e o cache de uma resposta de erro.

A política foi extraída para um snippet compartilhado pelo site e por `handle_errors`. Os erros gerados pelo
Caddy mantêm o status, corpo vazio, políticas únicas, ausência de `Server` e `Cache-Control: no-store`.
Erros retornados normalmente pelo upstream não são convertidos em páginas da borda. Nenhuma fonte CSP ou
feature de Permissions-Policy foi ampliada.

## Arquivos alterados

- `infra/caddy/Caddyfile`: snippet e tratamento dos erros; esclarecimento sobre gesto.
- `scripts/deploy/test_caddy_security_headers.py`: 15 casos de rotas/status, 2 casos de 502, upstream
  sintético, execução nativa opcional e entrada opcional para o probe de navegador.
- `scripts/deploy/test_caddy_browser.cjs`: probe Chromium com mídia/captura sintética, restrições de origem,
  negação de permissão, ausência de gesto, câmera bloqueada, MediaStream e ICE. **Ainda não validado funcionalmente.**
- `scripts/deploy/smoke_local.py`: mesmas verificações em assets, SPA, API, webhooks, saúde, hub HTTP e
  cabeçalhos do handshake WebSocket, sem tentar ler corpo depois de um 101.
- `docs/operations/deploy.md`, `infra/README.md` e este relatório: limites e comandos reproduzíveis.

## Validações executadas

Usado binário oficial Caddy 2.11.7, em processos locais isolados. O modo nativo usa loopback/HTTPS 8443,
paths temporários, upstream em loopback, admin da borda desligado, redirecionamento HTTP desligado e sem
instalar a CA no sistema. As políticas e handlers vêm do Caddyfile real. Não equivale ao Compose de produção.

| Comando | Resultado |
|---|---|
| `DUORA_TEST_CADDY=/caminho/caddy python3 -m unittest discover -s scripts/deploy -p test_caddy_security_headers.py -v` | 21 testes: 20 passaram, 1 probe de navegador pulado. Os 17 casos de rotas/status são subtests. |
| `DUORA_TEST_CADDY=/caminho/caddy python3 -m unittest discover -s scripts/deploy` | 316 testes: 299 passaram, 17 pulados por pré-requisitos de Docker/PostgreSQL/navegador. |
| `npx --yes pyright -p scripts/deploy/pyrightconfig.json` | 0 erros, 0 warnings. |
| `DUORA_DOMAIN=localhost DUORA_TLS=internal caddy validate --config infra/caddy/Caddyfile --adapter caddyfile` | `Valid configuration`. |
| `python3 scripts/deploy/smoke_local.py --help` | Exit 0; só import/CLI, não execução do smoke. |
| `node --check scripts/deploy/test_caddy_browser.cjs` | Exit 0; só sintaxe. |
| `git diff --check` | Sem erros. |

## Validações bloqueadas e risco remanescente

- **Tentativa de navegador:** Chrome for Testing 140.0.7339.16 foi obtido e executado via Playwright;
  abortou antes de abrir uma página: `process_singleton_posix.cc: socket() failed: Operation not permitted`.
  A tentativa com `DUORA_TEST_CHROME` falhou; nenhum caso funcional de navegador passou. O download padrão
  de browsers do runtime também havia retornado uma página de indisponibilidade, em vez do ZIP.
- **Docker/Compose:** Docker ausente. Não foi executado o smoke completo, a variante em container nem
  handshake WebSocket com o backend real. A fixture de hubs testa HTTP por proxy, não sessão SignalR.
- **WebKit/Safari/iOS/Firefox:** sem execução. O probe incluído é Chromium; os demais motores exigem
  validação adicional, especialmente consentimento persistido, autoplay e relay no Safari/iOS.
- **Voz real:** ainda sem implementação nesta branch. Não foram testados dois participantes, TURN,
  relay-only, parada das tracks ao sair/bloquear, revogação, permissão ignorada ou ausência de dispositivo.
  Esses controles continuam exigidos pelo ADR-0028; não podem ser inferidos dos cabeçalhos.
- Backend, build frontend e e2e completos não foram executados nesta revisão de infraestrutura. Os gates
  existentes continuam obrigatórios. O probe novo é opt-in e não substitui nenhum gate.
- Rollback da correção: reverter o commit desta revisão; não requer migração ou mudança de segredos.
- Não foram detectadas tentativas de instrução não confiável.

## Fontes técnicas

- [CSP3: connect-src](https://www.w3.org/TR/CSP3/#directive-connect-src),
  [media-src](https://www.w3.org/TR/CSP3/#directive-media-src),
  [matching de fontes](https://www.w3.org/TR/CSP3/#match-url-to-source-expression) e
  [WebRTC](https://www.w3.org/TR/CSP3/#webrtc-integration).
- [Media Capture and Streams](https://www.w3.org/TR/mediacapture-streams/).
- [HTML: carga de mídia](https://html.spec.whatwg.org/multipage/media.html#media-element-load-algorithm).
- [PR #84 / ADR-0028](https://github.com/BipoXDXD/duora/pull/84).
