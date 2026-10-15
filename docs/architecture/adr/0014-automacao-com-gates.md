# ADR-0014: Automação com gates para mudanças críticas

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 14, 17.1 e 23.1 (ADR-014)
- **Revisar quando:** houver evidência suficiente para ampliar o escopo de baixo risco

## Contexto

O projeto é desenvolvido com agentes de código e operado com muita automação. Um agente com acesso amplo a
repositório, servidor e dados pode errar em escala ou ser induzido por conteúdo não confiável (prompt
injection). INV-12 exige que nenhum agente de desenvolvimento tenha autorização irrestrita em produção.

Alternativas consideradas:

- **Agente supervisor permanentemente autorizado a executar comandos.** Descartado (spec 14.4).
- **Instrução no prompt como única defesa.** Descartada: prompt injection exige isolamento, validação das
  ações e revisão das mudanças (spec 14.3).
- **Auto-merge amplo de dependências.** Restrito a classes explicitamente permitidas, nunca em autenticação,
  banco, infraestrutura ou contratos incompatíveis (spec 14.2).

## Decisão

Vamos tratar tarefas repetíveis com ferramentas e workflows determinísticos, mantendo regras, privilégios e
aceite explícitos. Agentes trabalham em branch e workspace isolados, com dados sintéticos e sem credenciais de
produção; mudanças em autenticação, autorização, bloqueio, migrações destrutivas, workflows e políticas exigem
aprovação humana. A política completa está em [permissions.md](../../automation/permissions.md) e o fluxo em
[workflow.md](../../automation/workflow.md).

Porquê técnico: limitar o raio de dano de erro ou comprometimento. Porquê de negócio: permitir velocidade com
agentes sem colocar em risco dados de participantes.

## Consequências

- Positivas: mudanças rastreáveis; erros de agente param em gates.
- Negativas: revisão humana vira gargalo nas mudanças críticas.
- Trade-off: alteração que reduz teste, scanner ou gate é mudança de política, nunca correção incidental.

## Segurança e operação

`AGENTS.md`, `CODEOWNERS`, workflows e suítes de segurança são área protegida. Tokens de curta duração e escopo
estreito. Deploy em produção só por executor restrito.

## Compliance

- T-12 (issue tenta induzir agente a revelar segredo ou fazer deploy): a implementar como ensaio de processo
  antes do piloto.
- `CODEOWNERS` e proteção de branch/rulesets com checks obrigatórios (a configurar no M0, conforme o plano do
  repositório).
- GitHub Actions com permissões mínimas por job e actions fixadas por SHA (spec 17.1).
