# ADR-0006: Mesma origem para SPA, API e hub

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 8.1, 11.3, 15.3 e 23.1 (ADR-006)
- **Revisar quando:** uma separação de domínios for necessária

## Contexto

A sessão é um cookie `__Host-` (ADR-0005), que não aceita atributo `Domain`. SPA, API e hub precisam
compartilhá-lo. CORS com credenciais é fonte frequente de erro, e CORS não controla conexões WebSocket.

Alternativas consideradas:

- **API e hub em subdomínio próprio, com CORS e credenciais.** Descartado: aumenta a superfície de cookies e
  origens sem ganho no piloto.
- **CDN/WAF na frente da origem.** Condicional: só depois de revisar dados, cache, WebSockets e exposição da
  origem (spec 2.3).

## Decisão

Vamos servir a SPA, `/api/v1` e `/hubs/updates` num único domínio de aplicação. O Caddy termina TLS, serve os
estáticos e encaminha API e hub sem remover prefixos. Não há CORS aberto; qualquer origem adicional aprovada é
enumerada exatamente e testada com credenciais, nunca com curinga.

Porquê técnico: cookies, antiforgery e `Origin` ficam simples e testáveis. Porquê de negócio: menos
configuração a manter e menos classes de vulnerabilidade a auditar.

## Consequências

- Positivas: cookie `__Host-`, sem preflight CORS, um certificado.
- Negativas: SPA e API compartilham disponibilidade e limites da mesma borda; assets e API ficam acoplados a um
  domínio.
- Trade-off: mudar de domínio exige migração de sessão e reconfiguração da borda.

## Segurança e operação

`Origin` validado na negociação e no upgrade WebSocket. Forwarded Headers confiam só no proxy esperado. Kestrel
nunca é publicado diretamente; o Caddy é a única entrada pública. Erro de API nunca vira página HTML da SPA
(spec 11.3, 15.3).

## Compliance

- T-13 (CSRF e origem indevida): a implementar no M1.
- Teste de integração afirmando que respostas da API não trazem `Access-Control-Allow-Origin` para origem não
  aprovada: a implementar no M1.
- Revisão da configuração do Caddy (rotas `/api/*`, `/hubs/*`, assets com hash e fallback da SPA) em M0/M3.
