# ADR-0041 — Janela de autenticação recente de 1 hora, intervalo fechado

- **Status:** Accepted (2026-10-28), decisão de segurança do dono do produto.
- **Origem:** auth.md, "Reautenticação e step-up"; spec 4.3 e API-A13. Substitui o valor de 5 min, registrado
  como hipótese.
- **Revisar quando:** incidente ou revisão de ameaça mostrar abuso de sessão aberta em ação sensível, ou entrar
  autenticação por passkey que torne o step-up barato.

## Contexto

Operações sensíveis (A11, A14, A15, A16, exportação/exclusão e ações administrativas críticas) exigem prova
recente de senha ou de TOTP (`reauthenticated_at`, `totp_verified_at`). A janela era 5 min, hipótese, em
intervalo semiaberto. Na prática, moderação e administração pediam TOTP de novo a cada poucos minutos.

Alternativas consideradas:

- **Manter 5 min:** menor exposição de sessão sequestrada, mas step-up frequente cansa quem modera e empurra para
  atalhos (código à mão, sessões paralelas).
- **Igualar à inatividade da sessão (15/30 min):** acopla dois controles diferentes e ainda interrompe sessões
  longas de moderação.
- **1 hora (escolhida):** um step-up cobre um turno curto de trabalho; a inatividade da sessão continua limitando
  a exposição de sessão esquecida.

## Decisão

`RecentAuthentication.Window` passa a 1 hora, para senha recente e TOTP recente. O intervalo é **fechado**:
`[prova, prova + 1 h]`; a prova feita há exatamente 1 h vale, 1 h + 1 tick não, e prova no futuro nunca vale.
A decisão continua só no servidor, com o instante lido do banco a cada requisição. As sessões não mudam:
administrativa 15 min ociosa / 8 h absoluta; normal 30 min / 12 h.

## Consequências

- A janela passa da inatividade das duas sessões. A prova só dura a hora inteira enquanto a sessão estiver em uso;
  sessão ociosa expira antes e pede login novo (`401` `UNAUTHENTICATED`), não step-up. A prova não estende a
  sessão.
- Sessão sequestrada logo após um step-up tem até 1 h para ações sensíveis, contra 5 min antes. Mitigações que
  continuam: MFA obrigatório para privilegiados, TOTP (não código de recuperação) nas ações críticas, inatividade
  de 15 min na sessão administrativa, revogação por security stamp e limite absoluto.
- Residual do I3 cresce na mesma medida: login com código de recuperação libera A16 (e A15 para participante) por
  até 1 h enquanto a sessão viver.
- A sessão temporária do bootstrap administrativo tem até 1 h para trocar a senha, limitada também pela
  inatividade de 15 min; a mensagem da tela foi ajustada.

## Compliance

- `RecentAuthenticationTests`: janela de 1 h; fronteiras 1 h exata (válida) e 1 h + 1 tick (inválida).
- `AdminRouteTests`: TOTP de exatamente 1 h vale com a sessão em uso; após 1 h + 1 tick, `REAUTHENTICATION_REQUIRED`;
  sessão administrativa ociosa por 15 min responde `UNAUTHENTICATED` antes do fim da prova.
- `ReauthenticationTests`, `MfaEnrollmentTests`, `MfaLoginTests`, `AdminBootstrapTests`,
  `RoutesCatalogPublicationTests`: expiram a prova com `StepUpClock`, que mantém a sessão viva até a janela vencer.
- `PhotoReviewAccessTests`: TOTP de exatamente 1 h autoriza Decide/Appeal/Preserve; além dela, não.
