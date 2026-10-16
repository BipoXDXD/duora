# Autenticação, sessão e proteção do navegador — especificação do M1

Especificação do que o M1 implementa em identidade: cookie e ticket no servidor, expiração, MFA, CSRF,
recuperação, revogação, anti-enumeração, Data Protection e rate limit, mais o contrato das rotas API-A01 a
API-A16. Fontes: spec 4.3, 8.1, 8.2, 9.2, 9.3, 11.1–11.5 e 13.4 (A11Y-01); ADRs
[0005](../architecture/adr/0005-identity-local-cookie-ticket-persistente.md),
[0006](../architecture/adr/0006-mesma-origem-spa-api-hub.md) e
[0017](../architecture/adr/0017-mfa-por-papel.md); fichas D-AUTH-01, D-AUTH-02, D-AUTH-04, D-AUTH-05 e
D-SEC-03 ([sources.yml](../references/sources.yml)). Ameaças tratadas: TM-01 a TM-22, TM-45 e TM-64 do
[threat-model.md](threat-model.md). Tabelas: [model.md](../data/model.md#schema-identity).

Regra do projeto: nada de criptografia ou protocolo de autenticação próprio. Hash de senha, tokens de e-mail,
TOTP, cookie e antiforgery vêm do ASP.NET Core Identity e do framework (spec 2.1; *Secure APIs*, cap. 4.4).
Toda mudança neste documento ou no código correspondente exige revisão humana
([permissions.md](../automation/permissions.md)).

Valores numéricos marcados **(hipótese)** são iniciais e configuráveis, versionados e sujeitos a medição
(spec 4.3, 11.5). Mudá-los não exige mudar este documento, só a configuração revisada.

## Papéis e contas

| Tipo de conta | Papéis | MFA | Política de sessão |
|---|---|---|---|
| Participante | `participant` | Opcional, TOTP (ADR-0017) | Normal: 30 min ociosa, 12 h absoluta |
| Privilegiada | `moderator`, `product_admin`, `operator` (um ou mais) | Obrigatório e confirmado antes da primeira sessão administrativa | Administrativa: 15 min ociosa, 8 h absoluta |

**Decidido (D13, Accepted em 2026-10-15):** conta privilegiada nunca recebe o papel `participant`, e
vice-versa (spec 1.3). A regra é de dados: um trigger em `identity.user_roles` recusa a mistura e serializa
concessões simultâneas da mesma conta ([model.md](../data/model.md)). Assim a política de sessão é decidida pela
conta no login, sem "elevar" uma sessão de participante. Bootstrap e concessão de papel privilegiado: comando
operacional único, auditado, fora da API pública, com credencial temporária que expira e exige configurar MFA
(spec 11.1). **Pré-condição do bootstrap (I5):** até a primeira ativação do MFA, o reset de senha de conta
privilegiada fica bloqueado (o e-mail não pode virar caminho para tomar uma conta que ainda não tem segundo
fator); o PR do bootstrap implementa o bloqueio e o teste. **Decisão I2 (revisão do #38):** conta privilegiada
não acessa rota de participante (perfil, bloqueio, salas): a política padrão das rotas responde `403` a ela, e só
as rotas da própria conta (sessões, MFA, senha) e as `/admin` a aceitam. **Pendente (PR seguinte, decisão D4):** o comando, com senha temporária mostrada uma vez no terminal
do operador; até lá, papéis só entram por fixture nos testes e não existe rota que conceda papel.

## Ticket no servidor

```mermaid
sequenceDiagram
    autonumber
    participant B as Navegador
    participant W as Duora.Web
    participant S as ITicketStore (PostgreSQL)
    B->>W: POST /api/v1/auth/login (credenciais, X-XSRF-TOKEN)
    W->>W: Identity valida senha (+ TOTP se exigido)
    W->>S: StoreAsync: cria login_sessions (chave aleatória; ticket protegido)
    W-->>B: Set-Cookie __Host-duora = referência protegida (Data Protection)
    B->>W: requisição seguinte com cookie
    W->>S: RetrieveAsync(chave): sessão ativa? ociosa e absoluta válidas? stamp igual?
    S-->>W: ticket ou nada (falha fechada)
    W-->>B: 200 ou 401 (nunca redirect HTML)
```

| Item | Especificação |
|---|---|
| Cookie de autenticação | `__Host-duora`; `Secure`, `HttpOnly`, `Path=/`, sem `Domain`, `SameSite=Lax`; sem `Max-Age`/`Expires` (cookie de sessão do navegador; a validade real é a do servidor) |
| Conteúdo do cookie | Só a referência ao ticket (claim de chave de sessão), protegida pelo Data Protection; nenhuma claim de papel, e-mail ou elegibilidade legível ou confiável no cliente |
| `CookieAuthenticationOptions.SessionStore` | `PostgresTicketStore` sobre `identity.login_sessions`, que absorve `auth_tickets` (1:1, mesma vida; [ADR-0019](../architecture/adr/0019-implementacao-identidade-sessao-outbox.md)) (D-AUTH-02) |
| Chave do ticket | 256 bits de CSPRNG, codificada em base64url; gerada em `StoreAsync`; nunca reutilizada. O banco guarda só o SHA-256 da chave (`key_hash`) |
| Ticket armazenado | `AuthenticationTicket` serializado e protegido pelo Data Protection (`bytea`), nunca em claro; classe S |
| `RetrieveAsync` | Um único `UPDATE ... RETURNING ticket` que confere sessão + `users.security_stamp` e desliza a expiração ociosa. Devolve nada (→ `401`) se: sessão revogada, `idle_expires_at <= agora`, `absolute_expires_at <= agora` ou stamp da sessão diferente do atual. **Pendente:** recusar conta `DeletionPending` (hoje a sessão continua válida, e `/me` informa o estado) |
| Conta restrita ou suspensa | Sessão válida, mas a política de autorização limita a conta própria, segurança e recurso (spec 8.2) |
| Respostas da API | `401`/`403` em Problem Details; `OnRedirectToLogin` e `OnRedirectToAccessDenied` substituídos para nunca redirecionar a API para HTML (spec 8.2; D-AUTH-01) |
| Banco indisponível | Falha fechada: nunca aceita ticket sem consulta (TM-16). Hoje a resposta é `500` `INTERNAL_ERROR` sem detalhe; **pendente:** mapear para `503` |
| Relógio | `TimeProvider` injetado em toda decisão de expiração; `FakeTimeProvider` nos testes |
| Negar por padrão | Política de fallback exige usuário autenticado; rotas públicas numa allowlist explícita (API-A01 a A07, `/health/live`, `/health/ready`; webhooks quando existirem) (TM-64). O teste `EndpointAuthorizationTests` falha se uma rota nova nascer pública |

Custo aceito (ADR-0005, ADR-0019): uma consulta ao ticket store por requisição autenticada, que também grava
`last_seen_at` e a expiração ociosa. Limitar essa escrita a uma vez por minuto fica para quando a medição
mostrar custo.

## Expiração

| Política | Ociosa | Absoluta | Aplicação |
|---|---|---|---|
| Normal | 30 min | 12 h | Contas de participante |
| Administrativa | 15 min | 8 h | Contas privilegiadas: a política é gravada em `login_sessions.policy` no login, pelos papéis da conta |

- No login: `absolute_expires_at = agora + absoluta` (imutável) e `idle_expires_at = min(agora + ociosa,
  absolute_expires_at)`.
- Em cada requisição autenticada aceita: `idle_expires_at = min(agora + ociosa, absolute_expires_at)`. A
  renovação nunca ultrapassa a absoluta (spec 11.1; TM-13).
- Sem "lembrar para sempre" no padrão (spec 4.3). O cookie não persiste além do navegador.
- Expirada a sessão, o hub é fechado (`CloseOnAuthenticationExpiration`) e a SPA limpa dados privados e leva à
  reautenticação (spec 9.2, 13.5).

## Fixação e renovação

Nova sessão (nova linha em `login_sessions`, nova chave de ticket, novo cookie) e revogação da anterior em:
login, troca de senha (API-A16), conclusão de reset (API-A07 não emite sessão), ativação ou desativação de MFA
e qualquer mudança de papel. Nunca reaproveitar sessão anônima (spec 11.1; TM-03). Depois de login, logout ou
troca de identidade, o token antiforgery é renovado (spec 8.2).

Implementado no M1 para o login: `SignOutAsync` revoga a sessão anterior no banco e só **depois** a nova é
gravada pelo esquema `Duora.Session.SignIn`. O handler de cookie padrão, depois de autenticar, reaproveitaria a
chave anterior no `SignInAsync` (chama `RenewAsync`); o esquema separado evita isso, mas também lê o cookie da
requisição, então a garantia está na ordem. Invariante: nunca chamar SignIn sem o SignOut antes; os testes de
fixation falham se ela for quebrada (ADR-0019). Login e logout já devolvem um `XSRF-TOKEN` novo, ligado à
identidade nova.

## Reautenticação e step-up

API-A13 grava `login_sessions.reauthenticated_at` e, quando a prova inclui o TOTP, também `mfa_verified_at`. O
login grava os dois (o segundo só com MFA). A janela de autenticação recente é **5 min (hipótese)**, intervalo
semiaberto, verificada no servidor a cada requisição; o frontend não concede privilégio (spec 8.2). O ticket store
lê esses instantes, a política e os papéis atuais da conta do banco em cada requisição, nunca do ticket nem do
cliente: tirar um papel fecha as rotas `/admin` na requisição seguinte.

| Exige autenticação recente | Prova exigida |
|---|---|
| API-A09 com detalhes, A11, A14, A15, A16 | Senha; TOTP também quando a conta tem MFA |
| API-D01, D03, D04 (exportação e exclusão) | Senha; TOTP quando houver |
| Ações administrativas críticas: decisão de moderação (ADM14, ADM15), leitura de evidência (ADM13), publicar/cancelar evento (ADM04, ADM05), publicar/aposentar catálogo (ADM09, ADM10), pausas operacionais (ADM17, ADM18), reprocessar job (ADM20) | TOTP obrigatório |

Faltando autenticação recente: `401` com `code = REAUTHENTICATION_REQUIRED`, sem efeito. Implementação: filtros
`RequireRecentAuthentication()` (A11, A12, A15, A16) e `RequireRecentMfa()` (A14 e ações administrativas
críticas, conferindo `totp_verified_at`: só TOTP conta, código de recuperação não; decisão I3). A sessão guarda
três provas: `reauthenticated_at` (credencial), `mfa_verified_at` (qualquer segundo fator, que completa a sessão
privilegiada) e `totp_verified_at` (TOTP). Renovar a sessão (A12, A15, A16) herda o `absolute_expires_at` da
anterior: nunca estende a vida desde o login. As rotas ADM ainda não existem; cada uma acrescenta o filtro ao nascer.

## MFA

Conforme [ADR-0017](../architecture/adr/0017-mfa-por-papel.md): obrigatório para contas privilegiadas,
opcional (TOTP) para participantes, pelos mesmos endpoints.

| Tema | Especificação |
|---|---|
| Método | TOTP RFC 6238 (HMAC-SHA1, 30 s, 6 dígitos) sobre a chave do Identity, verificado por `Duora.Core.Identity.Totp` com tolerância de um passo para cada lado. O provider do Identity não diz qual passo aceitou, e sem o passo não há como recusar reuso (B2 da revisão do #38). Passkeys só depois, pelo suporte oficial e com teste (spec 11.1) |
| Cadastro (API-A11) | Exige autenticação recente; gera nova chave do autenticador e devolve `sharedKey` e URI `otpauth://` uma única vez, com `no-store`; MFA ainda **inativo**. Trocar a chave troca o security stamp (comportamento do Identity): a sessão que pediu o cadastro adota o stamp novo e as outras caem. MFA já ativo → `409` `MFA_ALREADY_ENABLED` |
| Confirmação (API-A12) | Código TOTP válido ativa o MFA, gera 10 códigos de recuperação **(hipótese)** mostrados uma vez, revoga as sessões da conta e abre uma nova para quem confirmou, já com a prova de MFA. Código errado → `400` `MFA_CODE_INVALID`; sem cadastro → `409` `MFA_ENROLLMENT_NOT_STARTED`. **Pendente:** notificar o titular por e-mail |
| Login com MFA (API-A04) | Mesmo corpo com `twoFactorCode` (6 dígitos; espaços aceitos) ou `recoveryCode` (até 32 caracteres; caixa, espaços e hífen ignorados), nunca os dois (`400`). Senha correta sem segundo fator → `401` `MFA_REQUIRED`, **sem** criar sessão. Segundo fator errado → `401` `INVALID_CREDENTIALS`, conta no mesmo lockout da senha e é auditado (`identity.second_factor_failed`); o lockout só zera depois do segundo fator certo. Toda a verificação roda com a linha da conta travada (`FOR NO KEY UPDATE`), para o contador não perder falhas sob logins paralelos (B1). **TOTP de uso único:** o passo aceito é gravado em `users.last_totp_step` por UPDATE condicional (`last_totp_step is null or last_totp_step < passo`), e o mesmo código, ou um passo anterior ainda na tolerância, é recusado em A04, A12 e A13 (B2). Código de recuperação é de uso único e o resgate é auditado |
| Conta privilegiada sem MFA confirmado | Login abre sessão administrativa restrita ao cadastro de MFA: A08, A11, A12 e A13 (a reautenticação entra porque A11 e A12 exigem autenticação recente); A05 e as rotas públicas não exigem sessão. Toda outra rota, inclusive `/admin/*` → `403` `MFA_ENROLLMENT_REQUIRED` |
| Novos códigos (API-A14) | Exige **TOTP** provado há menos de 5 min (`totp_verified_at`; login com código de recuperação não basta, decisão I3); troca o conjunto inteiro no mesmo commit, com a conta travada (pedidos simultâneos se serializam, sem `500`); mostra o novo uma vez; nada em log. Sem MFA ativo não há TOTP recente, então a resposta é `401` `REAUTHENTICATION_REQUIRED`; o `409` `MFA_NOT_ENABLED` fica como defesa do serviço |
| Desativar (API-A15) | Autenticação recente; recusado (`403` `MFA_REQUIRED_BY_ROLE`) se a conta é privilegiada; para participante, desativa, troca a chave por uma que ninguém viu, esvazia os códigos, revoga as sessões e abre uma nova para quem desativou. MFA já inativo → `204` sem efeito. **Pendente:** notificar o titular |
| Recuperação de MFA de conta privilegiada | Não há caminho self-service por e-mail: procedimento operacional auditado, com outra pessoa ou conta privilegiada quando houver (TM-22). O reset de senha não remove nem contorna o MFA (`AdminMfaTests.PasswordResetDoesNotBypassMfaForPrivilegedAccount`) |
| Armazenamento | Verificado: o Identity 10.0.13 grava chave e códigos em claro em `identity.user_tokens`, e ligar `ProtectPersonalData` exigiria um `ILookupProtector` próprio que também cifraria e-mail e nome de usuário. Decidido em 2026-10-15: **D2**, a chave do autenticador é protegida por Data Protection num `DuoraUserManager` (só no web), com o custo de que perder o key ring invalida o TOTP de todos (a chave ilegível é tratada como ausente); **D3**, os códigos de recuperação ficam só como hash PBKDF2 do `PasswordHasher`, um por código, num `IUserTwoFactorRecoveryCodeStore` próprio (`DuoraUserStore`), com o custo de até 10 verificações PBKDF2 por resgate |
| Auditoria | `ops.audit_events` na mesma transação: `identity.mfa_enrollment_started`, `mfa_enabled`, `mfa_disabled`, `mfa_recovery_codes_regenerated`, `mfa_recovery_code_used`, `reauthenticated` e `password_changed` |
| Risco residual | O key ring é ponto único de falha do TOTP (D2). O lock por conta serializa as verificações da mesma conta durante um hash; a espera acontece numa fila em memória por conta (`AccountCheckGate`), sem conexão, e só quem tem a vez abre a transação, então uma fila de verificações não esgota o pool nem o `max_connections` (antes, 25 logins paralelos seguravam 25 conexões, e o servidor respondia 53300 → `500`). A espera tem teto (`Identity:AccountLockTimeout`, **5 s, hipótese**, na fila e no `lock_timeout` da transação, que continua garantindo a exclusão entre processos), e passado o teto a rota responde `503` `TEMPORARILY_UNAVAILABLE` com `Retry-After`, sem gravar nada (A04, A11 a A16; `AccountLockTimeoutTests`). Um `503` só aparece com dezenas de verificações simultâneas da mesma conta, o que o rate limit por conta (`429`, igual para conta existente ou não) impede antes de virar canal de enumeração. **Aceito pelo responsável técnico (residual do I3):** login com código de recuperação ainda libera, por 5 min, o A16 e, para participante, o A15, porque esse é o caminho de quem perdeu o dispositivo; o A14 e as ações administrativas críticas continuam exigindo TOTP |

## CSRF

Cookie same-origin com `SameSite=Lax` reduz, mas não elimina CSRF; login e logout também são mutações
(spec 11.3; *Web Application Security*, 2ª ed., caps. 11 e 29). O M1 usa três camadas:

| Camada | Especificação |
|---|---|
| 1. Token antiforgery | `AddAntiforgery(o => o.HeaderName = "X-XSRF-TOKEN")` e um middleware (`CsrfProtectionMiddleware`) que chama `IAntiforgery.IsRequestValidAsync` em todo método que não é `GET`, `HEAD`, `OPTIONS` ou `TRACE`, **inclusive** API-A02 a A07 (D-AUTH-05). Roda depois da autorização: rota protegida sem sessão responde `401` antes. Cookie interno do antiforgery: `__Host-duora-af`, `HttpOnly`, `Secure`, `SameSite=Strict` |
| 2. Cookie legível | API-A01 chama `GetAndStoreTokens` e grava o request token no cookie `XSRF-TOKEN` (`Secure`, `Path=/`, `SameSite=Strict`, **sem** `HttpOnly`, para a SPA ler). A SPA o envia no header `X-XSRF-TOKEN` (já implementado em `frontend/src/shared/api/antiforgery.ts`). O token sozinho não basta: precisa casar com o cookie `__Host-` e com a identidade |
| 3. Origem | Em mutação: `Origin` presente e diferente da origem pública (`Duora:PublicOrigin`; em Development também `https://localhost:5173`) → rejeita; `Sec-Fetch-Site` presente e diferente de `same-origin` → rejeita; os dois ausentes → só a camada 1 decide (cliente não-navegador). No hub, `Origin` é obrigatório e exato (TM-45) |

- Falha de antiforgery ou de origem: `403` Problem Details com `code = CSRF_VALIDATION_FAILED`, sem efeito
  (o padrão do framework é `400`; o M1 mapeia para `403`).
- A resposta não diz qual camada falhou.
- Webhooks (`/webhooks/{provider}`, quando existirem) precisarão de exceção explícita no middleware, com
  revisão.
- O token é ligado à identidade: depois de login, logout ou troca de identidade, a SPA chama API-A01 de novo.
- CORS: nenhuma política aberta; respostas não trazem `Access-Control-Allow-Origin` para origem não aprovada
  (ADR-0006).

## Recuperação de senha

| Passo | Especificação |
|---|---|
| Pedido (API-A06) | Recebe e-mail; responde sempre `202` com corpo vazio e grava **sempre um** e-mail na outbox (mesmo trabalho nos três casos, TM-06): conta confirmada recebe o link; cadastro pendente recebe um lembrete para cadastrar de novo, sem link; endereço sem conta recebe um aviso de que não há conta. O volume é contido pelo rate limit por endereço (3 por hora) |
| Conta pendente | Reset **não** se aplica e **não** confirma o e-mail: o cadastro pendente pode ter sido feito por outra pessoa, com os aceites dela. Quem tem o e-mail conclui pelo cadastro, que substitui o pendente (ver [Cadastro pendente](#cadastro-pendente-e-pré-sequestro-de-conta)). API-A07 recusa conta não confirmada com `INVALID_TOKEN` |
| Token | `GeneratePasswordResetTokenAsync` do Identity (Data Protection, ligado ao security stamp). Provedor próprio para reset com vida de **30 min (hipótese)**, separado do token de confirmação de e-mail (**24 h, hipótese**). A entrega do e-mail expira antes (15 min e 12 h) para o link não chegar morto |
| Link | Rota da SPA com as credenciais no **fragmento**: `/redefinir-senha#userId=…&token=…` e `/confirmar-email#userId=…&token=…`. O fragmento não vai a servidor, log de proxy nem `Referer`. O token é o base64url dos bytes do token do Identity. **Pendente (frontend):** `Referrer-Policy: no-referrer` nas páginas de conta |
| Conclusão (API-A07) | `POST` com `userId`, `token` e `newPassword`. Sucesso: troca a senha, atualiza o security stamp (o token deixa de valer), zera o bloqueio por tentativas, revoga **todas** as sessões e não emite sessão nova. Falha: `400` `INVALID_TOKEN`, igual para conta inexistente. **Pendente:** notificar o titular da troca |
| Conta privilegiada | Reset troca a senha, mas não remove nem contorna o MFA (TM-22) |
| Conta com MFA | Reset **não** zera o lockout (I4): o e-mail não prova o segundo fator, e destravar reabriria a força bruta do TOTP. Conta sem MFA continua destravada pelo reset |
| Política de senha | Comprimento mínimo de 12 caracteres e máximo de 128 **(hipótese)**, sem regras de composição forçada; colar e gerenciador de senhas permitidos (A11Y-01); verificação contra senhas vazadas fica como evolução, se houver serviço aprovado |

Confirmação de e-mail (API-A03) usa o mesmo desenho: link para a SPA, ação por `POST` explícito (scanner de
e-mail que faz `GET` não confirma), token de uso único (confirmar troca o security stamp, então o mesmo link depois dá `INVALID_TOKEN`). Além do token, exige a
**senha** do cadastro (ver abaixo).

## Endereço de e-mail

Ameaça (TM-67): fora do ASCII, endereços diferentes colidem na normalização usada para achar a conta
(`"ſam@example.com".ToUpperInvariant()` é `SAM@EXAMPLE.COM`). Um reset pedido para a variante acharia a conta
de `sam@` e mandaria o link para a variante, o mesmo padrão do incidente do GitHub em 2019.

| Regra | Especificação |
|---|---|
| Formato aceito | Só ASCII imprimível na parte local e no domínio; domínio internacional entra em punycode (`xn--`). Fora disso, `400` `VALIDATION_FAILED` (`INVALID_EMAIL`), antes de qualquer busca. Escolhido por ser a regra mais simples que elimina a colisão por inteiro; comparar a forma normalizada com a original resolveria o caso do `ſ`, mas dependeria de acertar todas as tabelas de caixa do Unicode. O custo é recusar endereços com parte local internacional (SMTPUTF8), raros e ainda mal suportados pelos provedores |
| Destinatário | E-mails de conta (link de reset, aviso de conta existente, lembrete de cadastro pendente) vão para o endereço **gravado**, nunca para o digitado. Só o aviso de "não há conta" vai para o digitado, porque não há outro |

Testes: `LookalikeEmailTests`, `EmailAddressTests`.

### Idioma dos e-mails de conta

Os e-mails de conta saem em pt-BR (padrão) ou en, os idiomas da interface ([ADR-0022](../architecture/adr/0022-i18n-intl-messageformat-catalogo-tipado.md)).
A SPA manda o idioma ativo no `Accept-Language` de toda requisição; o cadastro (API-A02) e o pedido de reset
(API-A06) escolhem o idioma por esse header, só pelo idioma primário e só entre `pt` e `en`, e gravam a tag no
pedido da outbox (`language`). Header ausente, malformado, só com `q=0` ou com outro idioma dá pt-BR, sem erro
para quem chamou; pedido gravado sem o campo, ou com tag fora da lista, também sai em pt-BR. O idioma é o de quem
fez a requisição, não uma preferência guardada da conta: o aviso de "alguém tentou se cadastrar" sai no idioma
de quem tentou. Nenhum texto da requisição entra no e-mail; os links são os mesmos nos dois idiomas, com o token
no fragmento.

Testes: `EmailLanguageTests`, `LocalizedEmailDispatchTests`, `AccountEmailLanguageTests`, `EmailLanguageTagTests`.

## Cadastro pendente e pré-sequestro de conta

Ameaça (TM-66), nas duas ordens: (1) o atacante cadastra o e-mail da vítima com a senha dele e espera a vítima
confirmar; (2) a vítima cadastra, o atacante cadastra depois (a senha dele substitui a dela) e o link do cadastro
do atacante chega à caixa da vítima, que clica achando que é o dela.

| Regra | Especificação |
|---|---|
| Confirmação (API-A03) | `POST {userId, token, password}`. Só confirma se o token for válido **e** a senha bater com a do cadastro pendente: quem confirma prova posse do e-mail e conhecimento da senha, e um link gerado por outra pessoa é inofensivo. Senha errada, token inválido, conta inexistente e pendente vencido dão o mesmo `400` `INVALID_TOKEN`, com o mesmo corpo, e todo caminho custa um hash. Sem login automático: confirmar só muda o estado da conta, e a SPA leva ao login, que é o único caminho que abre sessão (e o único a proteger contra fixation) |
| Teto de tentativas | Cada falha numa conta pendente soma no contador do Identity (que essa conta não usa para mais nada) num único `UPDATE` atômico, que na 5.ª falha (`Identity:MaxConfirmationFailures`, **hipótese**) troca o security stamp no mesmo comando: o link deixa de valer e a pessoa cadastra de novo. Tentativas em paralelo não furam o teto. Rate limit por IP: 20 a cada 10 min |
| Uso único e corrida | Confirmar grava só se o stamp ainda for o verificado e troca o stamp: o link morre na hora, e depois da confirmação nenhuma senha (certa ou errada) muda a resposta, então o link não vira oráculo de senha. A conta pendente não tem sessão, então a troca do stamp não derruba ninguém. Recadastro concorrente faz a confirmação falhar como `INVALID_TOKEN`, sem `500` |
| Cadastro sobre conta pendente | Substitui o pendente: senha, aceites, e-mail digitado, data e security stamp novos. O stamp novo invalida todo link de confirmação anterior. Um único `INSERT ... ON CONFLICT (normalized_email) DO UPDATE` na mesma transação dos aceites e da outbox, o que também serializa cadastros simultâneos |
| Cadastro sobre conta confirmada | Nada muda na conta; o titular recebe um aviso pela outbox |
| Prazo do pendente | `Identity:PendingAccountLifetime`, **72 h (hipótese)**, contado do último cadastro. Vencido, a confirmação é recusada (`400` `INVALID_TOKEN`); login de conta não confirmada já falha sempre. Um cadastro novo substitui o pendente vencido. **Pendente:** job de limpeza dos pendentes vencidos |
| Nome de usuário | É o próprio e-mail; `CHECK (normalized_user_name IS NOT DISTINCT FROM normalized_email)` deixa a unicidade só no índice do e-mail, o único que o upsert arbitra |

Risco residual: quem conhece o e-mail de outra pessoa pode atrasar o cadastro dela, sobrescrevendo o pendente
ou esgotando as tentativas de confirmação (o link morre e ela cadastra de novo). Isso é negação de serviço,
contida pelo rate limit por e-mail e por IP, e não dá acesso: a vítima nunca confirma uma conta com senha que
ela não conhece, porque a confirmação pede a senha. Testes: `AccountTakeoverTests`.

## Revogação

| Gatilho | Efeito |
|---|---|
| Logout (API-A05) | Revoga a sessão corrente (`revoked_at`, motivo `logout`), apaga o ticket, expira o cookie, fecha as conexões do hub desta sessão |
| Revogar sessão (API-A10) | Só sessão da própria conta (alheia → `404`); mesmo efeito do logout naquela sessão |
| Troca de senha (API-A16) | Atualiza stamp; revoga todas as outras sessões; emite sessão nova para a corrente |
| Reset (API-A07), ativar/desativar MFA | Revoga todas as sessões (ou as outras, no caso de MFA feito na própria sessão) |
| Suspensão, exclusão (API-D03), medida de moderação que restringe a conta | Revoga todas as sessões, ou a conta passa à política restrita (spec 8.2, 8.4) |
| Restore de backup | Revoga todas as sessões antes de reabrir (spec 18.4; T-11) |
| Comprometimento do key ring | Novo key ring e revogação de todas as sessões; restaurar chave antiga não é resposta a invasão (spec 11.2) |

Hub: o processo web mantém um registro em memória `sessionId → connectionIds` (uma instância, ADR-0009). A
revogação, gravada no banco, aciona o fechamento dessas conexões no mesmo processo; uma revogação vinda do
worker (exclusão, sanção) chega por outbox de tempo real. Mesmo que o fechamento falhe, a próxima leitura ou
comando do cliente falha no `RetrieveAsync` (spec 9.2; T-14).

## Anti-enumeração

| Rota | Comportamento igual para conta existente e inexistente |
|---|---|
| API-A02 cadastro | `202` com corpo vazio. E-mail já cadastrado e confirmado: o titular recebe "alguém tentou se cadastrar com este e-mail"; pendente: o cadastro substitui o pendente e manda um link novo; novo: cria a conta. **Tempo:** os três casos fazem o mesmo trabalho (um hash, o mesmo upsert, os mesmos comandos de aceite, uma leitura, um token e uma linha de outbox), verificado por `EnumerationTimingTests` comparando comandos SQL e hashes |
| API-A04 login | `401` com `code = INVALID_CREDENTIALS` para e-mail inexistente, senha errada, e-mail não confirmado e conta bloqueada. **Tempo:** todo caminho de falha custa exatamente um hash (o `SignInManager` decide conta não confirmada e bloqueada antes do hash, então esses casos e a conta inexistente verificam um hash de isca pelo mesmo hasher); a senha errada numa conta existente ainda grava a falha para o lockout, e essa diferença fica encoberta por um piso de latência: nenhuma falha responde antes de `Identity:FailedLoginMinimumDuration` (**350 ms, hipótese**) mais até `FailedLoginJitter` (**100 ms**). `MFA_REQUIRED` só aparece depois da senha certa |
| API-A06 pedido de reset | `202` neutro. **Tempo:** os três casos (confirmada, pendente, sem conta) fazem uma leitura e gravam um e-mail na outbox |
| API-A03 e A07 | Token inválido, expirado ou de conta inexistente → mesmo erro |

`429` (rate limit) pode ser distinguível, porque depende de origem e volume, não da existência da conta
(*Secure APIs*, cap. 4.4; TM-06).

## Data Protection

| Item | Especificação (spec 11.2, 15.4; D-AUTH-02) |
|---|---|
| Persistência | Repositório em arquivo no diretório de `DataProtection:KeysDirectory` (em dev, `.local/dataprotection-keys/`, fora do git). Em produção, volume nomeado `dataprotection` (`0700`, dono = usuário do container web), fora da camada gravável do container. O boot confere em `/proc/self/mountinfo` que o diretório é ponto de montagem de um volume (nem `overlay`, nem `tmpfs`/`ramfs`); `DataProtection:RequireMountedVolume=false` só em Development, nos testes e na geração do OpenAPI (ambiente `BuildTimeOpenApi`); em qualquer outro ambiente o boot recusa o `false`, e o `appsettings.Development.json` não vai para o publish. Caminho absoluto e link simbólico resolvidos antes da checagem. **Limite:** a checagem prova que há um volume, não que ele é durável nem que tem backup: um volume anônimo do Docker (ou um `emptyDir`, se um dia houver Kubernetes) passa e some com `down -v`, `--renew-anon-volumes` ou a troca do pod; quem garante o volume nomeado é o `ComposeFileTests` |
| Proteção em repouso | `ProtectKeysWithCertificate` com o certificado do Docker secret `DataProtection__Certificate` (PEM com certificado e chave, gerado pelo `duora-secrets`); `UnprotectKeysWithAnyCertificate` com o atual e os de `DataProtection__PreviousCertificates` durante uma rotação. Custódia separada e rotação em [deploy.md](../operations/deploy.md#certificado-do-key-ring-spec-112). `DataProtection:RequireCertificate=false` só em Development, Testing e BuildTimeOpenApi; em qualquer outro ambiente o boot recusa o `false`. O boot também confere que o certificado chegou de fato ao Data Protection (`XmlEncryptor` é `CertificateXmlEncryptor`), exige RSA e um só par no atual. Num volume que já tem chaves, ligar o certificado não recifra as antigas: key ring novo ([deploy.md](../operations/deploy.md#certificado-do-key-ring-spec-112)) |
| Nome da aplicação | `SetApplicationName("Duora")`, estável entre deploys. **Pendente (M3):** nome por ambiente; hoje homologação e produção se separam por volumes e certificados diferentes |
| Vida da chave | 90 dias (`SetDefaultKeyLifetime`, igual ao padrão, explícito) com rotação automática; o framework nunca apaga chave antiga, então ela continua lendo o que protegeu (cookies de no máximo 12 h, tokens de no máximo 24 h) |
| Worker | Não recebe o key ring de cookies; usa referências de conta e jobs. Se um dia precisar desproteger algo, usa purpose próprio e o ADR registra |
| Backup | Key ring e certificado no inventário de backup, com custódia separada do backup do banco (spec 18.3) |
| Startup | Web falha ao subir sem `DataProtection:KeysDirectory` (`WebDoesNotStartWithoutAPersistentKeyRing`), com ele fora de um volume montado (`WebDoesNotStartWithTheKeyRingOutsideAMountedVolume`, `KeyRingMountTests`) ou com `RequireMountedVolume=false` fora de Development, Testing e BuildTimeOpenApi (`OnlyDevelopmentTestsAndBuildMayTurnOffTheMountedVolumeRequirement`, Production, Staging e qualquer outro nome); exigir o certificado entra com a proteção em repouso (spec 11.4) |
| Comprometimento | Novo key ring, revogação de todas as sessões e invalidação de tokens pendentes. Revogar chaves (`IKeyManager.RevokeAllKeys`) num processo vivo só vale depois que o `KeyRingProvider` recarrega o key ring: o próprio processo recarrega em segundo plano e ainda pode servir o ring antigo enquanto isso; outro processo sobre o mesmo volume só percebe no refresh periódico (24 h, padrão do framework). Na resposta a incidente, revogar e **reiniciar o web** |

Teste: T-15. `KeyRingRecoveryTests` recria o host sobre o mesmo banco: com o mesmo diretório a sessão continua
válida; depois de uma rotação a chave antiga segue lendo a sessão e a nova protege o que é novo; com diretório vazio
ou chaves revogadas a sessão termina em `401 UNAUTHENTICATED`, nunca `500`, e um login novo funciona; ticket
ilegível no banco também dá `401` com aviso no log. `KeyRingLossTests`: o par antiforgery anterior dá `403
CSRF_VALIDATION_FAILED`, `GET /auth/csrf` com o cookie antigo emite um par novo que funciona, e links de
confirmação e reset dão `400 INVALID_TOKEN`. Vida da chave e nome da aplicação fixados por teste.
`ComposeFileTests` confere que o diretório do `compose.prod.yml` é o volume montado só no web, e o smoke local
recria o container web e confere que um token antiforgery emitido antes continua válido. `KeyRingCertificateTests`
confere a proteção em repouso: a chave no volume está cifrada; o volume sem o certificado, ou com outro, não abre a
sessão (`401`); com o mesmo certificado ou depois de uma rotação (atual novo, anterior em
`PreviousCertificates`) a sessão continua válida, e a chave nova nasce cifrada com o certificado novo.

## Rate limit e lockout

Duas camadas (spec 11.5; *Secure APIs*, cap. 5.1): **lockout por conta no banco** (Identity, sobrevive a
reinício e não depende de IP) e **rate limiter em memória** por IP e por conta-alvo (`AddRateLimiter` e
`PartitionedRateLimiter`, D-AUTH-04), que reinicia com o processo (aceito no piloto, ADR-0009). Os limites
ficam na seção `RateLimiting` da configuração. O IP do cliente vem de `X-Forwarded-For` só quando o remetente
está numa rede de `ReverseProxy:KnownNetworks` (em produção, a rede `edge` do Caddy, `172.30.10.0/24`, conforme
`docs/operations/deploy.md`), com `ForwardLimit = 1`; o middleware roda antes do rate limit. Sem rede
configurada, o processamento fica **desligado**: com as listas vazias o framework aceitaria o header de qualquer
remetente. Rede inválida impede o boot. Testes: `ForwardedHeadersTests`,
`StartupConfigurationTests.WebDoesNotStartWithAnInvalidProxyNetwork`.

| Operação | Chave | Limite inicial (hipótese) | Resposta |
|---|---|---|---|
| Login (A04): falhas de senha | Conta (banco) | 20 falhas seguidas → bloqueio de 15 min; zera no sucesso e no reset de senha | `401` `INVALID_CREDENTIALS` (sem revelar lockout) |
| Login (A04) | E-mail normalizado, exista a conta ou não | 10 / 15 min | `429` + `Retry-After` |
| Login (A04) | IP | 10 / min | `429` + `Retry-After` |
| Cadastro (A02) | E-mail normalizado | 3 / hora | `429` + `Retry-After` |
| Cadastro (A02) | IP | 20 / hora | `429` + `Retry-After` |
| Pedido de reset (A06) | E-mail normalizado | 3 / hora | `429` + `Retry-After` |
| Pedido de reset (A06) | IP | 10 / hora | `429` + `Retry-After` |
| Conclusão de reset (A07), confirmação (A03) | IP | 20 / 10 min | `429` + `Retry-After` |
| Reautenticação (A13) | Conta (banco) e conta (memória) | Mesmo lockout da conta, com a conta travada; e o limite por conta abaixo | `401`; `429` + `Retry-After` |
| Reautenticação e MFA (A11 a A15), troca de senha (A16) | Conta (um contador para as seis rotas, `RateLimiting:AccountSecurityPerAccount`; abrir outra sessão não renova) | 10 / hora | `429` + `Retry-After` |
| Revogar sessão (A10) | Sessão | 30 / hora | `429` + `Retry-After` |
| Bloquear (API-S01) | Conta (id da sessão; vale para todas as sessões e IPs da conta) | 30 / hora (`RateLimiting:BlockPerAccount`, decisão G6) | `429` + `Retry-After` |
| Enviar foto (API-M01) | Conta e IP, cada um com o seu limite | 20 / hora em cada um (`RateLimiting:PhotoUploadPerAccount` e `PhotoUploadPerIp`, **hipótese**, ADR-0025) | `429` + `Retry-After`, antes de ler o corpo |

O limite do bloqueio fica no handler, depois da autenticação (o middleware de rate limit roda antes dela e não
conhece a conta), e também é em memória: nenhuma quota crítica depende dele (spec 11.5). Testes:
`BlockRateLimitTests`. O do envio de foto segue o mesmo modelo, no handler e antes de ler o corpo; a trava que
sobrevive ao restart é o teto de pendentes no banco. Testes: `PhotoUploadRateLimitTests`.

Valores calibrados pelo uso real e versionados; nunca bloquear permanentemente uma conta por falhas de terceiros
(TM-09). Trade-off aceito ([ADR-0019](../architecture/adr/0019-implementacao-identidade-sessao-outbox.md)):
quem insiste no e-mail de outra pessoa esgota o limite por conta dela e pode bloqueá-la por até 15 min. Não há
lockout progressivo, que facilitaria o bloqueio abusivo; o reset de senha destrava a conta. **Pendente:** aviso
por e-mail ao titular quando a conta é bloqueada. Métricas: contagem de `401`/`429` por rota e origem agregada, sem e-mail nem ID de pessoa como label
(spec 19.2).

## Contrato de rotas API-A01 a A16

Todas sob `/api/v1`, JSON UTF-8, `Cache-Control: no-store`, erros em Problem Details (spec 8.1). "Antiforgery"
inclui a checagem de origem da seção [CSRF](#csrf). "Recente" = autenticação recente na janela de
[step-up](#reautenticação-e-step-up).

| ID | Rota | Autenticação | Antiforgery | Recente | Entrada → saída | Erros principais | Auditoria |
|---|---|---|---|---|---|---|---|
| API-A01 | `GET /auth/csrf` | Pública | — | — | — → `204` + cookie `XSRF-TOKEN` | — | — |
| API-A02 | `POST /auth/register` | Pública | Sim | — | `email` (≤ 254), `password` (12–128), `acceptedTermsVersion`, `acceptedPrivacyVersion` (1–32, `[A-Za-z0-9._-]`) → `202` sem corpo, igual exista ou não a conta | `400` `VALIDATION_FAILED` (com `errors`) ou `INVALID_REQUEST`; `403` `CSRF_VALIDATION_FAILED`; `429` `RATE_LIMITED` | Pendente |
| API-A03 | `POST /auth/confirm-email` | Pública | Sim | — | `userId`, `token`, `password` (até 128) → `204` (uso único; não abre sessão) | `400` `INVALID_TOKEN` (token inválido ou senha errada, sem distinguir); `400` `VALIDATION_FAILED`; `403`; `429` | Pendente |
| API-A04 | `POST /auth/login` | Pública | Sim | — | `email`, `password` (até 128), `twoFactorCode?` (6 dígitos) ou `recoveryCode?` (até 32) → `204` + cookie `__Host-duora` + `XSRF-TOKEN` novo | `401` `INVALID_CREDENTIALS`, `401` `MFA_REQUIRED`; `400`; `403`; `429` | Resgate de código de recuperação; login pendente |
| API-A05 | `POST /auth/logout` | Pública (sem sessão também responde `204`) | Sim | — | — → `204` + `XSRF-TOKEN` novo; revoga a sessão no servidor | `403` | Pendente |
| API-A06 | `POST /auth/password-reset-requests` | Pública | Sim | — | `email` → `202` sem corpo | `400`; `403`; `429` por IP e por e-mail | Pendente |
| API-A07 | `POST /auth/password-resets` | Pública | Sim | — | `userId`, `token`, `newPassword` (12–128) → `204` | `400` `INVALID_TOKEN`, `400` `VALIDATION_FAILED`; `403`; `429` | Pendente |
| API-A08 | `GET /me` | Cookie (inclusive conta restrita) | — | — | → `id`, `email`, `status` (`PendingEmail`, `PendingEligibility`, `Active`, `Restricted`, `Suspended`, `DeletionPending`), `emailConfirmed`, `mfaEnabled`, `roles` | `401` `UNAUTHENTICATED` | — |
| API-A09 | `GET /me/sessions` | Cookie | — | Só para detalhes (pendente) | → `items[]`: `id`, `createdAt`, `lastSeenAt`, `current`, `description` (só família de navegador e sistema; o User-Agent não é guardado); até 100 sessões ativas | `401` | — |
| API-A10 | `DELETE /me/sessions/{sessionId}` | Cookie | Sim | — | → `204` (também para sessão própria já revogada) | `404` `NOT_FOUND` (alheia ou inexistente); `403`; `401` | Pendente |
| API-A11 | `POST /me/mfa/enrollments` | Cookie | Sim | Sim | → `200` `sharedKey`, `authenticatorUri` | `401` `REAUTHENTICATION_REQUIRED`; `409` `MFA_ALREADY_ENABLED`; `429` | `identity.mfa_enrollment_started` |
| API-A12 | `POST /me/mfa/confirmations` | Cookie | Sim | Sim | `code` (6 dígitos) → `200` `recoveryCodes[]` + sessão renovada | `400` `MFA_CODE_INVALID` ou `VALIDATION_FAILED`; `401`; `409` `MFA_ALREADY_ENABLED`/`MFA_ENROLLMENT_NOT_STARTED`; `429` | `identity.mfa_enabled` |
| API-A13 | `POST /auth/reauthenticate` | Cookie (inclusive sessão restrita) | Sim | — | `password`, `twoFactorCode?` (obrigatório se a conta tem MFA) → `204` | `401` `INVALID_CREDENTIALS`; `400` | `identity.reauthenticated` |
| API-A14 | `POST /me/mfa/recovery-codes` | Cookie | Sim | Sim | → `200` `recoveryCodes[]` (conjunto anterior revogado) | `409` `MFA_NOT_ENABLED`; `401`; `429` | `identity.mfa_recovery_codes_regenerated` |
| API-A15 | `DELETE /me/mfa` | Cookie | Sim | Sim | → `204` (também se já estava inativo) + sessão renovada | `403` `MFA_REQUIRED_BY_ROLE`; `401`; `429` | `identity.mfa_disabled` |
| API-A16 | `POST /me/password-changes` | Cookie | Sim | Sim | `currentPassword`, `newPassword` (12–128) → `204` + sessão renovada; outras sessões revogadas | `400` validação; `401` `INVALID_CREDENTIALS` (senha atual errada, conta no lockout); `429` | `identity.password_changed` |

Regras comuns: DTO estrito (campo desconhecido ou repetido → `400` `INVALID_REQUEST`); corpo até 32 KiB; erros
em `application/problem+json` com `type` (`urn:duora:problem:<código>`), `title`, `status`, `code` e `traceId`;
instantes em RFC 3339 UTC com `Z`; nenhum segredo (senha, token, código, chave) em log,
trace ou métrica; nenhuma credencial em query string; respostas de sucesso não ecoam e-mail nem dados de
outras contas; contrato publicado pelo OpenAPI gerado (ADR-0018).

## Testes do M1

| Teste | Cobre |
|---|---|
| T-01 | Conta pendente, suspensa ou sem requisito etário negada nas rotas de participação |
| T-13 | Login, logout e escrita sem antiforgery ou com origem indevida → `403`, sem efeito |
| T-14 | Sessão revogada ou expirada com hub ativo: novas ações negadas, conexão fechada |
| T-15 | Container recriado e rotação de chave: sessões conforme a política |
| T-21 | Logout de A e login de B no mesmo navegador: nada de A visível a B |
| Verificações do ADR-0017 | `/admin/*` nega conta sem MFA; A15 não remove MFA de conta privilegiada; participante ativa e desativa TOTP |
| Testes "a criar" do [threat-model.md](threat-model.md#6-ameaça--mitigação--teste) | TM-01 a TM-22, TM-45 e TM-64 |

## Estado de implementação

Preencher no PR que implementar cada item, com link para o teste e a evidência do runner. Nenhum status muda
sem artefato do CI (spec 25.19).

| Item | Status | Teste / evidência | PR |
|---|---|---|---|
| Cookie `__Host-duora` e `ITicketStore` PostgreSQL | implementado | `SessionTests` (atributos, revogação) | feat/m1-identity |
| Expiração ociosa e absoluta, normal e administrativa | implementado | `SessionTests.IdleSessionExpiresAfterThirtyMinutes`, `ActivitySlidesTheIdleLimitButNeverPastTwelveHours`, `AdminSessionTests` | feat/m1-identity, feat/m1-admin-mfa |
| Fixação: nova sessão no login e nas mudanças de privilégio | login, ativação e desativação de MFA e troca de senha implementados; mudança de papel exige login novo (sessão normal não abre `/admin`) | `SessionTests.SessionIdChangesOnLoginAndThePreviousCookieStopsWorking`, `MfaEnrollmentTests.ConfirmationActivatesMfaReturnsTenCodesAndRenewsTheSession`, `ReauthenticationTests.ChangingThePasswordRenewsThisSessionAndEndsTheOthers`, `AdminRouteTests.ARoleGrantedDuringANormalSessionNeedsANewAdministrativeLogin` | feat/m1-admin-mfa |
| Reautenticação e step-up | implementado (A13, filtros de recência); rotas ADM ainda não existem | `ReauthenticationTests`, `AdminRouteTests.ACriticalActionNeedsASecondFactorProvedInTheLastFiveMinutes` | feat/m1-admin-mfa |
| MFA TOTP (A11, A12, A14, A15) e política por papel | implementado; notificação por e-mail pendente | `MfaEnrollmentTests`, `MfaLoginTests`, `AdminMfaTests`, `AdminRouteTests`, `RoleSeparationTests`, `SecretLeakTests.MfaSecretsAppearOnlyInTheResponsesThatProvisionThem` | feat/m1-admin-mfa |
| Troca de senha (A16) | implementado; notificação por e-mail pendente | `ReauthenticationTests` | feat/m1-admin-mfa |
| Antiforgery, cookie `XSRF-TOKEN` e checagem de origem | implementado | `CsrfProtectionTests` (T-13) | feat/m1-identity |
| Recuperação de senha e confirmação de e-mail | implementado (e-mail pela outbox; adaptador de desenvolvimento em arquivo) | `RegistrationTests`, `PasswordResetTests`, `TransactionalEmailDispatchTests` | feat/m1-identity |
| Revogação (A05, A10, troca de senha, reset) e fechamento do hub | A05, A10 e reset implementados; troca de senha e hub pendentes | `SessionTests` (T-14, parte HTTP), `PasswordResetTests` | feat/m1-identity |
| Anti-enumeração (resposta e tempo) | implementado | `RegistrationTests`, `LoginTests`, `PasswordResetTests`, `EnumerationTimingTests` | feat/m1-identity |
| Pré-sequestro de conta (TM-66) | implementado | `AccountTakeoverTests` | feat/m1-identity |
| Data Protection persistente e protegido | implementado: volume e certificado exigidos no boot; nome por ambiente pendente | `StartupConfigurationTests`, `KeyRingRecoveryTests`, `KeyRingCertificateTests`, `KeyRingMountTests`, `ComposeFileTests` | feat/dataprotection-cert |
| Rate limit e lockout | implementado | `LoginTests`, `RegistrationTests` | feat/m1-identity |
| Contrato A01 a A16 no OpenAPI gerado | publicado | `docs/api/openapi.json` | feat/m1-identity, feat/m1-admin-mfa |

## Histórico

- **2026-10-15, feat/m1-identity.** Contrato reconciliado com a implementação, que é a fonte do contrato
  ([ADR-0018](../architecture/adr/0018-contrato-openapi-code-first.md)); o frontend foi escrito contra a mesma
  versão. Mudanças em relação ao texto anterior:
  - API-A02: `termsVersion`/`privacyVersion` viram `acceptedTermsVersion`/`acceptedPrivacyVersion`; `202` sem
    corpo.
  - API-A03 e A07: `CONFIRMATION_TOKEN_INVALID` e `RESET_TOKEN_INVALID` viram um código só, `INVALID_TOKEN`;
    A07 identifica a conta por `userId` (vindo do link), não por `email`.
  - API-A05: pública; responde `204` mesmo sem sessão.
  - API-A08: `state` e `eligibility` viram `status` e `emailConfirmed`; `roles` e `mfaEnabled` mantidos.
  - API-A09: `sessionId` e `deviceSummary` viram `id` e `description`; `networkPrefix` não existe.
  - Links de e-mail com `userId` e `token` no fragmento.
  - Sessão: `auth_tickets` absorvida por `login_sessions`; expiração ociosa gravada a cada requisição.
  - Lockout: 20 falhas e 15 min, sem dobrar; tabela de rate limit trocada pelos valores implementados.
  - CSRF: middleware próprio com `IsRequestValidAsync` em vez de `UseAntiforgery()`, que só valida formulário.
  - Data Protection: nome de aplicação `Duora`; proteção em repouso e nome por ambiente ficam para o M3.
- **2026-10-15, feat/m1-identity (revisão de segurança).** Duas falhas corrigidas antes do merge:
  - Pré-sequestro de conta (TM-66): o cadastro sobre conta pendente passa a substituí-la; o pendente expira
    em 72 h; reset não serve a conta pendente nem confirma o e-mail.
  - Enumeração por tempo (TM-06): cadastro e pedido de reset fazem o mesmo trabalho em todos os casos (o reset
    passa a mandar um e-mail também para endereço sem conta); login com falha custa sempre um hash e respeita um
    piso de latência.
  - API-A03 passa a exigir `password` (`{userId, token, password}`), fechando a ordem inversa do pré-sequestro
    (vítima cadastra, atacante cadastra, vítima clica no link do atacante); 5 falhas invalidam o link.
- **2026-10-15, feat/m1-dataprotection-t15.** T-15: o web não sobe com o key ring fora de um volume montado
  (`/proc/self/mountinfo`); vida da chave explícita em 90 dias; testes de recriação, rotação, key ring vazio e
  revogado. Proteção em repouso e nome por ambiente continuam pendentes, à espera de decisão.
- **2026-10-15, feat/dataprotection-cert.** Proteção em repouso (decisão do responsável técnico: certificado X.509
  como Docker secret): `ProtectKeysWithCertificate` com o certificado do `duora-secrets`, anteriores só para
  decifrar durante a rotação (`rotate-certificate`, `retire-previous-certificates`), boot falha sem certificado.
- **2026-10-15, feat/m1-admin-mfa.** MFA administrativo, com as decisões do responsável técnico:
  - D1 (= D13 do model.md): contas privilegiada e de participante disjuntas, por trigger no banco.
  - D2: chave TOTP protegida por Data Protection no `DuoraUserManager`; D3: códigos de recuperação com hash
    PBKDF2, um por código, no `DuoraUserStore`.
  - D4: bootstrap administrativo fora deste PR (o seguinte traz a senha temporária).
  - D5: empilhado sobre o PR da auditoria (`ops.audit_events`).
  - D6: grupo `/admin` com a política de sessão administrativa, testado com sondas registradas pelo mesmo ponto de
    extensão que os módulos usarão; nenhuma rota ADM de produto entra aqui.
  - Mudanças em relação ao texto anterior: segundo fator errado no login é `INVALID_CREDENTIALS`; a sessão restrita
    inclui A13; A11 troca o security stamp (outras sessões caem); A15 em conta sem MFA é `204`; os limites de
    A11, A12 e A14 a A16 dividem um contador por sessão.
- **2026-10-16, feat/m1-admin-mfa (revisão independente).** Dois bloqueantes e os importantes corrigidos:
  - B1: lockout com a linha da conta travada em toda verificação de credencial (A04, A13, A12, A14 a A16).
  - B2: TOTP de uso único por `users.last_totp_step` e verificador RFC 6238 próprio que devolve o passo.
  - I1: o cadastro grava o papel `participant` na mesma transação, com backfill na migration.
  - I2 e I3: decisões do assistente na ausência do usuário (conta privilegiada fora das rotas de participante;
    `totp_verified_at` separado e exigido no step-up e no A14).
  - I4: reset não zera o lockout de conta com MFA; segundo fator errado é auditado. I5: pré-condição do bootstrap.
  - Menores: limite por conta (não por sessão) também no A13, renovação herda o limite absoluto, concorrência no
    A12/A14 sem `500`, normalização de espaços e hífen.
- **2026-10-16, fix/mfa-lock-timeout.** Seguimento da re-revisão do #38: teto configurável na espera pelo lock da
  conta, com `503` `TEMPORARILY_UNAVAILABLE` e `Retry-After`; migration `ParticipantRoleBackfill` (idempotente)
  para contas cadastradas sem papel por uma instância antiga durante o deploy; residual do I3 aceito e registrado.
- **2026-10-16, fix/mfa-account-gate.** O teste de 30 logins paralelos deu `500` no CI do #43: com cada espera pelo
  lock segurando uma conexão, a suíte em paralelo esgotou o `max_connections` (53300). A espera passa a ser numa fila
  em memória por conta, sem conexão; o lock no banco continua. Teste:
  `AccountLockTimeoutTests.QueuedChecksOfTheSameAccountWaitWithoutHoldingDatabaseConnections` (25 na main, 1 aqui).
