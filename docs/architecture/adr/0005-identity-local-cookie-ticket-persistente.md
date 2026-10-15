# ADR-0005: ASP.NET Core Identity local, cookie e ticket persistente

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 2.1, 8.2, 11.1, 11.2 e 23.1 (ADR-005)
- **Revisar quando:** a carga operacional ou um requisito justificar OIDC gerenciado

## Contexto

O produto precisa revogar sessões individualmente (logout, troca de senha, bloqueio, exclusão, suspeita de
comprometimento) e fechar conexões do hub junto. Contas, confirmação de e-mail, recuperação e MFA
administrativo fazem parte da primeira entrega. O frontend é same-origin (ADR-0006).

Alternativas registradas na spec:

- **JWT guardado em `localStorage`.** Descartado: exposto a XSS e sem revogação imediata (spec 11.1).
- **OIDC gerenciado.** Mantido como alternativa arquitetural, não como segundo login simultâneo. Se adotado:
  handler oficial, Authorization Code + PKCE, validação de emissor e associação da conta por `issuer + subject`
  ([D-OPT-05](../../references/sources.yml)).
- **Servidor OAuth próprio.** Fora da primeira entrega (spec 1.1).
- **Criptografia de autenticação própria.** Proibida: hash, confirmação, recuperação e MFA vêm do framework.

## Decisão

Vamos usar ASP.NET Core Identity com cookie de autenticação `__Host-duora` (`Secure`, `HttpOnly`, `Path=/`,
sem `Domain`, `SameSite=Lax`) e `ITicketStore` persistido no PostgreSQL. O cookie carrega só uma referência
aleatória e opaca; o ticket protegido fica no servidor, com expiração ociosa e absoluta persistidas. Sessão
normal: 30 min ociosa / 12 h absoluta; administrativa: 15 min / 8 h, com MFA e step-up (spec 4.3, valores
iniciais configuráveis). Quem precisa de MFA está em [ADR-0017](0017-mfa-por-papel.md).

Porquê técnico: revogação imediata por sessão e nenhum token legível pelo JavaScript. Porquê de negócio:
controle operacional explícito sobre contas, sem depender de fornecedor de identidade no piloto.

## Consequências

- Positivas: revogação por sessão, security stamp revalidado, MFA e recuperação pelo framework.
- Negativas: o produto assume a responsabilidade por segurança e recuperação de contas; o key ring de Data
  Protection vira estado operacional que precisa de backup e custódia.
- Trade-off: cada requisição autenticada consulta o ticket store; o custo é aceito em troca da revogação.

## Segurança e operação

Antiforgery em toda rota cookie que altera estado, inclusive login e logout. Nova sessão após login e após
mudança de privilégio. Key ring persistente, protegido em repouso, nunca compartilhado entre homologação e
produção. Bootstrap administrativo por comando operacional auditado, fora da API pública (spec 11.1–11.3).

## Compliance

- T-13 (CSRF), T-14 (revogação com hub ativo), T-15 (Data Protection após recriar container) e T-21 (troca de
  identidade no mesmo navegador): a implementar no M1.
- Revisão humana e testes de segurança obrigatórios para mudanças em autenticação (spec 14.2).
