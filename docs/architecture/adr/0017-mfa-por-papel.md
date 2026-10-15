# ADR-0017: MFA obrigatório para papéis privilegiados e opcional para participantes

- **Status:** Accepted (2026-10-15)
- **Origem:** lacuna entre spec 8.2 (API-A11 a API-A15) e 11.1 (MFA administrativo); decisão do responsável
  técnico
- **Revisar quando:** incidentes de tomada de conta de participantes ou exigência regulatória pedirem MFA
  obrigatório para todos

## Contexto

A spec 11.1 só exige MFA administrativo (TOTP e recuperação controlada), mas os endpoints de MFA da 8.2
(`/me/mfa/...`) estão sob `/me` e valem para qualquer conta. Moderadores leem evidências sensíveis e aplicam
sanções, com o mesmo risco das contas administrativas. Para participantes, MFA protege conversas e perfil, mas
torná-lo obrigatório aumenta o atrito de entrada.

Alternativas consideradas:

- **Endpoints de MFA só para contas privilegiadas.** Descartada: impede participantes de protegerem a própria
  conta e exigiria uma segunda superfície de autorização nas mesmas rotas.
- **MFA obrigatório para todos.** Descartada no piloto: atrito na entrada e recuperação de conta mais cara,
  sem incidente que justifique.

## Decisão

Vamos exigir MFA para os papéis administrativos (administrador de produto, operador técnico) e de moderação.
Para participantes, MFA é opcional, com TOTP, pelos mesmos endpoints API-A11 a API-A15. Conta privilegiada
sem MFA confirmado não abre sessão administrativa; a API-A15 nunca remove o único método exigido de uma conta
privilegiada.

Porquê técnico: uma só implementação de MFA, com a obrigatoriedade decidida por política de papel. Porquê de
negócio: proteger quem acessa dados sensíveis sem aumentar o atrito de quem só participa.

## Consequências

- Positivas: mesma superfície de rotas e testes para todos; moderação protegida como administração.
- Negativas: participante sem MFA continua dependente só da senha e da recuperação por e-mail.
- Trade-off: a política de autorização precisa conhecer o papel para decidir se MFA é exigido.

## Segurança e operação

Segredo de configuração e códigos de recuperação nunca vão a log (spec 8.2). Ativação só após prova de posse.
Sessão administrativa: 15 min ociosa / 8 h absoluta, com step-up para ações críticas (spec 4.3).

## Compliance

- Teste HTTP: moderador ou administrador sem MFA confirmado é negado nas rotas `/admin/*` (M1).
- Teste HTTP: API-A15 recusa remover o único método de conta privilegiada (M1).
- Teste HTTP: participante ativa e desativa TOTP pelos mesmos endpoints (M1).
- Revisão humana obrigatória por tocar autenticação ([permissions.md](../../automation/permissions.md)).
