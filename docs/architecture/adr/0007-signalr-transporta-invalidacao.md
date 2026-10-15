# ADR-0007: SignalR transporta invalidação, não a verdade do jogo

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 2.1, 9 e 23.1 (ADR-007)
- **Revisar quando:** o perfil de tráfego exigir protocolo diferente

## Contexto

Salas, mensagens e decisões mudam enquanto a pessoa está conectada. Notificações podem se perder, duplicar ou
chegar fora de ordem, e um bloqueio ou revogação precisa cortar o acesso imediatamente, inclusive a dados que
já estariam enfileirados num socket.

Alternativas consideradas:

- **Hub como canal de comandos e de estado completo.** Descartado: dados privados enfileirados sobreviveriam à
  revogação, e a recuperação dependeria da entrega do socket.
- **Métodos genéricos no hub** (`JoinGroup(groupName)`, `SendToUser(userId, payload)`). Proibidos (spec 9.1).

## Decisão

Vamos usar o hub autenticado `/hubs/updates` só para notificações mínimas de mudança (`eventId`, `eventType`,
`schemaVersion`, `resourceId`, `resourceVersion`, `occurredAt`). Comandos de negócio continuam em HTTP. O
cliente busca mensagens, pistas e decisões pela API autorizada e reconcilia por versão. Acompanhar sala ou
conversa é autorizado como qualquer leitura.

Porquê técnico: o banco é a fonte da verdade (INV-07); o socket não precisa garantir entrega. Porquê de
negócio: privacidade e recuperação previsíveis em rede instável.

## Consequências

- Positivas: notificação perdida não perde estado; revogação vale na próxima leitura; payloads do hub não
  carregam dado privado.
- Negativas: cada notificação relevante gera uma leitura HTTP; o cliente precisa coalescer invalidações.
- Trade-off: latência ponta a ponta inclui uma ida à API (meta proposta p95 ≤ 1 s no ensaio, spec 19.1).

## Segurança e operação

Limites de conexões por conta/sessão, tamanho de mensagem e tempo sem autenticação válida. Sessão expirada ou
revogada fecha a conexão (spec 9.3). A publicação parte só do processo web (ADR-0003).

## Compliance

- T-05 (payload sem informação proibida, inclusive em notificação), T-07 (evento perdido, duplicado ou fora de
  ordem) e T-14 (sessão revogada com hub ativo): a implementar em M1/M2.
- Teste que enumera os métodos públicos do hub e rejeita métodos genéricos de grupo ou envio: a implementar
  quando o hub existir (M1).
