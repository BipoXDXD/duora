# ADR-0019: Implementação da identidade, das sessões e da outbox de e-mail no M1

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 8.2, 10.6, 11.1 a 11.5 e 19.2; [ADR-0005](0005-identity-local-cookie-ticket-persistente.md);
  [ADR-0008](0008-postgresql-para-jobs-e-outbox.md)
- **Revisar quando:** houver mais de uma instância web, um provedor de e-mail real, MFA ou incidente de
  bloqueio abusivo de contas

## Contexto

O ADR-0005 decidiu Identity local com cookie `__Host-duora` e `ITicketStore` no PostgreSQL. Faltava decidir
onde o adaptador mora, como a sessão é validada e revogada, como o CSRF e o rate limit se combinam com o login,
e como o e-mail de conta sai sem dual write. As decisões abaixo são as que alguém vai perguntar "por que é
assim?" daqui a um ano.

Alternativas consideradas:

- **Adaptador de identidade no projeto Web.** Descartada: o `DuoraDbContext` é interno à Infrastructure e o
  cadastro precisa gravar conta, aceites e outbox na mesma transação. Expor o contexto ao Web quebraria a
  fronteira que o compilador hoje garante.
- **Identity API endpoints prontos (`MapIdentityApi`).** Descartada: não fazem resposta neutra no cadastro,
  não têm CSRF para cookie e emitem token bearer, que a spec 11.1 recusa.
- **`SecurityStampValidator` com intervalo.** Descartada: com o ticket no banco, conferir o stamp na mesma
  consulta que já é feita a cada requisição custa nada e revoga na hora, sem janela de 30 minutos.
- **Lockout padrão do Identity (5 falhas, 5 minutos).** Descartada: com 5 tentativas qualquer pessoa tranca a
  conta de outra.
- **Gerar o token do e-mail no worker.** Descartada: o worker precisaria do key ring do Web para emitir tokens
  que o Web valida, o que a spec 11.2 pede para evitar.

## Decisão

Vamos implementar assim:

1. **Onde.** O adaptador de Identity (modelo, ticket store, casos de uso de conta) fica na Infrastructure, que
   passa a referenciar o framework compartilhado do ASP.NET Core; o worker herda essa referência e roda no
   mesmo runtime do web. O Web só expõe rotas, CSRF e rate limit.
2. **Sessão.** `login_sessions` absorve `auth_tickets` (spec 10.2): são 1:1 e têm a mesma vida. O cookie leva
   uma chave aleatória de 256 bits; o banco guarda só o SHA-256 dela e o ticket protegido por Data Protection.
   Cada requisição autenticada executa um único `UPDATE ... RETURNING` que confere revogação, expiração ociosa
   (30 min), absoluta (12 h) e o security stamp atual da conta, e desliza a ociosa com
   `least(agora + 30 min, absoluta)`. Revogação, logout e troca de senha valem já na requisição seguinte.
3. **Fixation.** O handler de cookie que já leu o cookie da requisição reaproveita a chave anterior no
   `SignInAsync` (chama `RenewAsync`, não `StoreAsync`). Por isso o login grava a sessão nova por um segundo
   esquema de cookie (`Duora.Session.SignIn`), que nunca autentica, com o mesmo cookie e o mesmo formato de
   ticket. A sessão anterior é revogada antes.
4. **CSRF.** Toda mutação, inclusive login e logout, exige o token antiforgery no header `X-XSRF-TOKEN`; como
   defesa adicional, `Origin` e `Sec-Fetch-Site`, quando presentes, têm de apontar para a origem pública (e, só
   em Development, `https://localhost:5173`). Qualquer falha é 403 `CSRF_VALIDATION_FAILED`, sem dizer qual
   verificação falhou. A verificação roda depois da autorização: rota protegida sem sessão responde 401 antes.
   Login e logout reemitem o `XSRF-TOKEN`, porque o token é ligado à identidade.
5. **Força bruta e bloqueio.** Rate limit em memória por IP (login 10/min) e por conta-alvo (login 10 por
   15 min; cadastro e pedido de reset 3 por hora), contado pelo e-mail normalizado exista a conta ou não. O
   lockout do Identity fica alto e curto (20 falhas seguidas, 15 minutos) e silencioso: a resposta é a mesma da
   senha errada. O reset de senha zera o bloqueio.
6. **Anti-enumeração.** Cadastro e pedido de reset respondem 202 com corpo vazio; login responde o mesmo 401
   `INVALID_CREDENTIALS` para conta inexistente, senha errada, e-mail não confirmado e conta bloqueada. Contra o
   canal de tempo, iguala-se o trabalho em vez de processar o cadastro no worker: cadastro e pedido de reset
   executam os mesmos comandos SQL, o mesmo número de hashes e uma linha de outbox em todos os casos (o reset
   passa a mandar e-mail também para endereço sem conta); no login, toda falha custa um hash e só responde depois
   de um piso de 350 ms com jitter, que encobre a gravação da falha feita só na senha errada de conta existente.
   O cadastro assíncrono no worker foi descartado porque o worker precisaria do key ring do web para emitir o
   token de confirmação (spec 11.2).
7. **E-mail.** Cadastro e reset gravam o pedido de e-mail na outbox (`ops.outbox_events` e
   `ops.outbox_deliveries`) na mesma transação. O worker toma lotes com `FOR UPDATE SKIP LOCKED` e lease, envia
   fora da transação e só encerra a entrega se o token de fencing for o atual **e** o lease ainda valer. O id da
   entrega é a chave de idempotência do envio. O token vai no payload e o payload é esvaziado quando a entrega
   termina. Os links levam `userId` e `token` no fragmento (`#`), que não vai ao servidor nem ao `Referer`; o
   prazo de entrega (12 h para confirmação, 15 min para reset, cujo token vale 30 min) fica abaixo da validade
   do token.
8. **Data Protection.** Nome de aplicação estável e key ring no diretório de `DataProtection:KeysDirectory`,
   obrigatório no boot. Só o web usa Data Protection.
9. **Cadastro pendente (pré-sequestro, TM-66).** O cadastro é um upsert atômico no e-mail normalizado: conta
   pendente é substituída (senha, aceites, stamp e data), conta confirmada fica intacta. O pendente expira em
   72 h; reset de senha não serve a conta pendente nem confirma o e-mail. O nome de usuário segue o e-mail por
   `CHECK`, e só o índice do e-mail é único, porque o upsert arbitra um índice só.

Porquê técnico: revogação imediata com uma consulta por requisição, nenhuma credencial legível pelo
JavaScript, nenhum e-mail perdido ou duplicado entre banco e envio. Porquê de negócio: conta e sessão
controladas pelo produto, sem fornecedor de identidade e sem atrito extra para o participante.

## Consequências

- Positivas: logout, revogação por dispositivo e troca de senha derrubam a sessão na hora; o banco vazado não
  entrega cookies válidos (só hashes); e-mail sai uma vez mesmo com retry, worker concorrente ou lease vencido.
- Negativas: uma escrita no banco por requisição autenticada (`last_seen_at` e expiração ociosa); limites em
  memória reiniciam com o processo e não valem entre réplicas; o token de uso único fica no banco até a entrega
  terminar.
- Trade-off: quem insiste no e-mail de outra pessoa também esgota o limite por conta dela e pode deixá-la
  bloqueada por até 15 minutos. Aceito porque o limite por conta é o único freio contra força bruta
  distribuída por muitos IPs; o reset de senha continua disponível e destrava a conta.

## Segurança e operação

- Cookie `__Host-duora`: `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, sem `Domain` e sem expiração no
  navegador. `XSRF-TOKEN`: `Secure`, `SameSite=Strict`, `Path=/`, legível pelo JavaScript. Respostas de
  `/api` com `Cache-Control: no-store`.
- Senha, token e conteúdo de e-mail nunca vão a log; falhas do envio registram só o tipo da exceção.
- Atrás do proxy, o rate limit por IP depende de Forwarded Headers configurado só para o proxy confiável
  (pendente, antes do primeiro deploy).
- O key ring precisa de backup e proteção em repouso (pendente, M3).

## Compliance

- T-13 e T-14 (parte HTTP), fixation, anti-enumeração, canário em resposta e log, BOLA em sessões, DTO
  estrito, limites e rate limit: `backend/tests/Duora.IntegrationTests/Identity/`.
- Outbox (entrega única, retry, concorrência, fencing, lease vencido, deadline): 
  `backend/tests/Duora.IntegrationTests/Outbox/`.
- Nomes snake_case nos schemas `identity` e `ops`: `MigrationsTests.PhysicalNamesAreSnakeCase`.
- Rotas fora da allowlist pública exigem sessão: `EndpointAuthorizationTests`.
- Revisão humana obrigatória (autenticação, [permissions.md](../../automation/permissions.md)).
