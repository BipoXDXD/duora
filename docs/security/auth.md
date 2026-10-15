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

**Proposta — confirmar:** conta privilegiada nunca recebe o papel `participant`, e vice-versa. A spec 1.3 já
exige contas distintas para responsabilidades distintas; tornar isso regra de dados (constraint na atribuição
de papel) permite decidir a política de sessão pela conta, sem "elevar" uma sessão de participante. Bootstrap
e concessão de papel privilegiado: comando operacional único, auditado, fora da API pública, com convite que
expira e exige configurar MFA (spec 11.1).

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
| Administrativa | 15 min | 8 h | Contas privilegiadas (**pendente:** chega com os papéis e o MFA) |

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

Implementado no M1 para o login: a sessão anterior é revogada e a nova é gravada pelo esquema
`Duora.Session.SignIn`, que nunca leu o cookie da requisição. O handler de cookie padrão, depois de autenticar,
reaproveitaria a chave anterior no `SignInAsync` (chama `RenewAsync`), o que seria fixation (ADR-0019). Login
e logout já devolvem um `XSRF-TOKEN` novo, ligado à identidade nova.

## Reautenticação e step-up

API-A13 grava `login_sessions.reauthenticated_at`. A janela de autenticação recente é **5 min (hipótese)**,
verificada no servidor; o frontend não concede privilégio (spec 8.2).

| Exige autenticação recente | Prova exigida |
|---|---|
| API-A09 com detalhes, A11, A14, A15, A16 | Senha; TOTP também quando a conta tem MFA |
| API-D01, D03, D04 (exportação e exclusão) | Senha; TOTP quando houver |
| Ações administrativas críticas: decisão de moderação (ADM14, ADM15), leitura de evidência (ADM13), publicar/cancelar evento (ADM04, ADM05), publicar/aposentar catálogo (ADM09, ADM10), pausas operacionais (ADM17, ADM18), reprocessar job (ADM20) | TOTP obrigatório |

Faltando autenticação recente: `401` com `code = REAUTHENTICATION_REQUIRED`, sem efeito.

## MFA

Conforme [ADR-0017](../architecture/adr/0017-mfa-por-papel.md): obrigatório para contas privilegiadas,
opcional (TOTP) para participantes, pelos mesmos endpoints.

| Tema | Especificação |
|---|---|
| Método | TOTP do Identity (`AuthenticatorTokenProvider`); passkeys só depois, pelo suporte oficial e com teste (spec 11.1) |
| Cadastro (API-A11) | Exige autenticação recente; gera nova chave do autenticador e devolve `sharedKey` e URI `otpauth://` uma única vez, com `no-store`; MFA ainda **inativo** |
| Confirmação (API-A12) | Código TOTP válido ativa o MFA, gera 10 códigos de recuperação **(hipótese)** mostrados uma vez, renova a sessão e notifica o titular |
| Login com MFA (API-A04) | Mesmo corpo com `twoFactorCode` ou `recoveryCode`. Senha correta sem segundo fator → `401` `MFA_REQUIRED`, **sem** criar sessão. Falhas de TOTP contam no mesmo lockout da senha |
| Conta privilegiada sem MFA confirmado | Login só abre sessão restrita ao cadastro de MFA (A11, A12, A05, A08); toda rota `/admin/*` → `403` `MFA_ENROLLMENT_REQUIRED` |
| Novos códigos (API-A14) | Autenticação recente; revoga o conjunto anterior; mostra o novo uma vez; nada em log |
| Desativar (API-A15) | Autenticação recente; recusado (`403` `MFA_REQUIRED_BY_ROLE`) se a conta é privilegiada; para participante, desativa, revoga as outras sessões e notifica |
| Recuperação de MFA de conta privilegiada | Não há caminho self-service por e-mail: procedimento operacional auditado, com outra pessoa ou conta privilegiada quando houver (TM-22) |
| Armazenamento | Chave do autenticador e códigos de recuperação ficam em `identity.user_tokens` (classe S). **Pendente, antes de implementar o MFA:** verificar se o Identity 10.0.13 os grava em claro; se sim, avaliar `ProtectPersonalData`/proteção por Data Protection, porque um dump do banco daria o segundo fator |

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
| Política de senha | Comprimento mínimo de 12 caracteres e máximo de 128 **(hipótese)**, sem regras de composição forçada; colar e gerenciador de senhas permitidos (A11Y-01); verificação contra senhas vazadas fica como evolução, se houver serviço aprovado |

Confirmação de e-mail (API-A03) usa o mesmo desenho: link para a SPA, ação por `POST` explícito (scanner de
e-mail que faz `GET` não confirma), token de uso controlado e resposta idempotente. Além do token, exige a
**senha** do cadastro (ver abaixo).

## Cadastro pendente e pré-sequestro de conta

Ameaça (TM-66), nas duas ordens: (1) o atacante cadastra o e-mail da vítima com a senha dele e espera a vítima
confirmar; (2) a vítima cadastra, o atacante cadastra depois (a senha dele substitui a dela) e o link do cadastro
do atacante chega à caixa da vítima, que clica achando que é o dela.

| Regra | Especificação |
|---|---|
| Confirmação (API-A03) | `POST {userId, token, password}`. Só confirma se o token for válido **e** a senha bater com a do cadastro pendente: quem confirma prova posse do e-mail e conhecimento da senha, e um link gerado por outra pessoa é inofensivo. Senha errada, token inválido, conta inexistente e pendente vencido dão o mesmo `400` `INVALID_TOKEN`, com o mesmo corpo, e todo caminho custa um hash. Sem login automático: confirmar só muda o estado da conta, e a SPA leva ao login, que é o único caminho que abre sessão (e o único a proteger contra fixation) |
| Teto de tentativas | Cada falha numa conta pendente conta no contador do Identity (que essa conta não usa para mais nada); na 5.ª falha (`Identity:MaxConfirmationFailures`, **hipótese**) o security stamp muda e o link deixa de valer; a pessoa cadastra de novo. Rate limit por IP: 20 a cada 10 min |
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
| Persistência | Repositório em arquivo no diretório de `DataProtection:KeysDirectory` (em dev, `.local/dataprotection-keys/`, fora do git). Em produção, volume persistente restrito (`0700`, dono = usuário do container web), fora da camada gravável do container |
| Proteção em repouso | **Pendente (M3):** `ProtectKeysWithCertificate` com certificado lido de arquivo de segredo; persistir não é criptografar |
| Nome da aplicação | `SetApplicationName("Duora")`, estável entre deploys. **Pendente (M3):** nome por ambiente; hoje homologação e produção se separam por volumes e certificados diferentes |
| Vida da chave | Padrão do framework (90 dias) com rotação automática; chaves antigas mantidas enquanto houver payload válido (cookies de no máximo 12 h, tokens de no máximo 24 h) |
| Worker | Não recebe o key ring de cookies; usa referências de conta e jobs. Se um dia precisar desproteger algo, usa purpose próprio e o ADR registra |
| Backup | Key ring e certificado no inventário de backup, com custódia separada do backup do banco (spec 18.3) |
| Startup | Web falha ao subir sem `DataProtection:KeysDirectory` (teste `WebDoesNotStartWithoutAPersistentKeyRing`); exigir o certificado entra com o M3 (spec 11.4) |
| Comprometimento | Novo key ring, revogação de todas as sessões e invalidação de tokens pendentes |

Teste: T-15 (recriar container mantém sessões válidas; rotação não derruba sessões dentro da política).

## Rate limit e lockout

Duas camadas (spec 11.5; *Secure APIs*, cap. 5.1): **lockout por conta no banco** (Identity, sobrevive a
reinício e não depende de IP) e **rate limiter em memória** por IP e por conta-alvo (`AddRateLimiter` e
`PartitionedRateLimiter`, D-AUTH-04), que reinicia com o processo (aceito no piloto, ADR-0009). Os limites
ficam na seção `RateLimiting` da configuração. **Pendente (antes do deploy):** Forwarded Headers aceitos só do
Caddy; sem isso, atrás do proxy, todo cliente cai na mesma partição de IP.

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
| Reautenticação (A13) | Sessão | Mesmo lockout da conta | `401` |
| MFA (A11, A12, A14, A15), troca de senha (A16) | Sessão | 10 / hora | `429` + `Retry-After` |
| Revogar sessão (A10) | Sessão | 30 / hora | `429` + `Retry-After` |

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
| API-A03 | `POST /auth/confirm-email` | Pública | Sim | — | `userId`, `token`, `password` (até 128) → `204` (idempotente; não abre sessão) | `400` `INVALID_TOKEN` (token inválido ou senha errada, sem distinguir); `400` `VALIDATION_FAILED`; `403`; `429` | Pendente |
| API-A04 | `POST /auth/login` | Pública | Sim | — | `email`, `password` (até 128) → `204` + cookie `__Host-duora` + `XSRF-TOKEN` novo. `twoFactorCode`/`recoveryCode` chegam com o MFA | `401` `INVALID_CREDENTIALS`, `401` `MFA_REQUIRED`; `400`; `403`; `429` | Pendente |
| API-A05 | `POST /auth/logout` | Pública (sem sessão também responde `204`) | Sim | — | — → `204` + `XSRF-TOKEN` novo; revoga a sessão no servidor | `403` | Pendente |
| API-A06 | `POST /auth/password-reset-requests` | Pública | Sim | — | `email` → `202` sem corpo | `400`; `403`; `429` por IP e por e-mail | Pendente |
| API-A07 | `POST /auth/password-resets` | Pública | Sim | — | `userId`, `token`, `newPassword` (12–128) → `204` | `400` `INVALID_TOKEN`, `400` `VALIDATION_FAILED`; `403`; `429` | Pendente |
| API-A08 | `GET /me` | Cookie (inclusive conta restrita) | — | — | → `id`, `email`, `status` (`PendingEmail`, `PendingEligibility`, `Active`, `Restricted`, `Suspended`, `DeletionPending`), `emailConfirmed`, `mfaEnabled`, `roles` | `401` `UNAUTHENTICATED` | — |
| API-A09 | `GET /me/sessions` | Cookie | — | Só para detalhes (pendente) | → `items[]`: `id`, `createdAt`, `lastSeenAt`, `current`, `description` (só família de navegador e sistema; o User-Agent não é guardado); até 100 sessões ativas | `401` | — |
| API-A10 | `DELETE /me/sessions/{sessionId}` | Cookie | Sim | — | → `204` (também para sessão própria já revogada) | `404` `NOT_FOUND` (alheia ou inexistente); `403`; `401` | Pendente |
| API-A11 | `POST /me/mfa/enrollments` | Cookie | Sim | Sim | → `sharedKey`, `authenticatorUri` | `401` `REAUTHENTICATION_REQUIRED`; `409` MFA já ativo | Início de cadastro |
| API-A12 | `POST /me/mfa/confirmations` | Cookie | Sim | Sim | `code` → `recoveryCodes[]` + sessão renovada | `400` `MFA_CODE_INVALID` | MFA ativado |
| API-A13 | `POST /auth/reauthenticate` | Cookie | Sim | — | `password`, `twoFactorCode?` → `204` | `401` `INVALID_CREDENTIALS`; `429` | Reautenticação |
| API-A14 | `POST /me/mfa/recovery-codes` | Cookie | Sim | Sim | → `recoveryCodes[]` (conjunto anterior revogado) | `409` MFA inativo | Códigos regenerados |
| API-A15 | `DELETE /me/mfa` | Cookie | Sim | Sim | → `204` | `403` `MFA_REQUIRED_BY_ROLE` | MFA desativado |
| API-A16 | `POST /me/password-changes` | Cookie | Sim | Sim | `currentPassword`, `newPassword` → `204` + sessão renovada | `400` validação; `401` senha atual errada | Senha trocada |

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
| Expiração ociosa e absoluta, normal e administrativa | normal implementada; administrativa pendente | `SessionTests.IdleSessionExpiresAfterThirtyMinutes`, `ActivitySlidesTheIdleLimitButNeverPastTwelveHours` | feat/m1-identity |
| Fixação: nova sessão no login e nas mudanças de privilégio | login implementado; mudança de privilégio pendente | `SessionTests.SessionIdChangesOnLoginAndThePreviousCookieStopsWorking` | feat/m1-identity |
| Reautenticação e step-up | não implementado | — | — |
| MFA TOTP (A11, A12, A14, A15) e política por papel | não implementado | — | — |
| Antiforgery, cookie `XSRF-TOKEN` e checagem de origem | implementado | `CsrfProtectionTests` (T-13) | feat/m1-identity |
| Recuperação de senha e confirmação de e-mail | implementado (e-mail pela outbox; adaptador de desenvolvimento em arquivo) | `RegistrationTests`, `PasswordResetTests`, `TransactionalEmailDispatchTests` | feat/m1-identity |
| Revogação (A05, A10, troca de senha, reset) e fechamento do hub | A05, A10 e reset implementados; troca de senha e hub pendentes | `SessionTests` (T-14, parte HTTP), `PasswordResetTests` | feat/m1-identity |
| Anti-enumeração (resposta e tempo) | implementado | `RegistrationTests`, `LoginTests`, `PasswordResetTests`, `EnumerationTimingTests` | feat/m1-identity |
| Pré-sequestro de conta (TM-66) | implementado | `AccountTakeoverTests` | feat/m1-identity |
| Data Protection persistente e protegido | persistente; proteção em repouso pendente (M3) | `StartupConfigurationTests.WebDoesNotStartWithoutAPersistentKeyRing` | feat/m1-identity |
| Rate limit e lockout | implementado | `LoginTests`, `RegistrationTests` | feat/m1-identity |
| Contrato A01 a A16 no OpenAPI gerado | A01 a A10 publicados; A11 a A16 pendentes | `docs/api/openapi.json` | feat/m1-identity |

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
