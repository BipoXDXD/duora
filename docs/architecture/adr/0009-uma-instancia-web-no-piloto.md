# ADR-0009: Uma instância web ativa no piloto

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 2.3, 9.3, 17.3 e 23.1 (ADR-009)
- **Revisar quando:** disponibilidade ou capacidade exigirem backplane e múltiplas instâncias. Gatilho
  registrado (issue #65): a autenticação depende de estado local, e uma segunda réplica traz de volta
  `503` entre instâncias pela espera no `FOR NO KEY UPDATE`, o oráculo de paralelismo para e-mail
  inexistente e rate limit e fila valendo por instância
  ([concorrência](../../data/concurrency.md#com-mais-de-uma-instância-web))

## Contexto

O hub SignalR mantém conexões em memória do processo. Banco compartilhado não distribui conexões entre
réplicas. Limitadores em memória reiniciam com o processo e não coordenam réplicas. A VPS única já é ponto
único de falha.

Alternativas consideradas:

- **Duas ou mais instâncias web com backplane/fan-out.** Adiado: exige fan-out, afinidade quando o transporte
  pedir, revogação distribuída e limites compartilhados.
- **Blue-green com duas web ativas no deploy.** Descartado sem esse planejamento: salas distribuídas entre
  instâncias incompatíveis recebem eventos parciais (spec 9.3, 17.3).

## Decisão

Vamos rodar uma única instância web ativa no piloto. O deploy aceita uma breve interrupção planejada e não é
chamado de zero-downtime.

Porquê técnico: notificações, sessões e limites locais ficam coerentes sem backplane. Porquê de negócio:
evitar custo e complexidade de escala parcial antes de medir a necessidade.

## Consequências

- Positivas: um publicador da outbox de tempo real; nenhuma divergência entre réplicas.
- Negativas: o deploy interrompe o serviço por alguns instantes; a capacidade do hub é a de um processo.
- Trade-off: quotas críticas e unicidades ficam no banco, não no limitador local (spec 11.5).

## Segurança e operação

O procedimento de deploy pausa novas rodadas, drena salas, pausa aquisição de jobs e reabre gradualmente
(spec 17.3).

## Compliance

- Compose de produção sem réplicas do serviço web: revisão de infraestrutura (a implementar em M0/M3).
- `docs/operations/deploy.md` descreve a interrupção planejada (a escrever com o pipeline de release).
- Qualquer proposta de segunda instância exige novo ADR que substitua este.
