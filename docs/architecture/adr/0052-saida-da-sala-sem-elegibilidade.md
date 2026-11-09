# ADR-0052: Saída da sala sem exigir elegibilidade

- **Status:** Proposed (2026-11-09). Toca autorização: o aceite vem com a revisão humana no merge.
- **Origem:** API-R04, "Sempre permitir saída segura; liberar reserva" (`spec.md:578`); INV-01 (`spec.md:55`)
- **Relaciona:** a política aplicada ao grupo de participação (`ParticipationPolicies`, vinda do ADR-0024, hoje
  superado pelo ADR-0029); [ADR-0028](0028-voz-ao-vivo-webrtc-relay-cloudflare-turn.md), que decide o contrário para a voz
- **Revisar quando:** outro estado de conta passar a existir com sala aberta, a saída ganhar efeito além de abortar e
  liberar, ou a voz (API-R10) for implementada

## Contexto

`POST /rooms/{roomId}/exit` herdava `EligibleParticipant` do grupo. Quem deixava de ser elegível com a sala aberta recebia
`403` e não conseguia sair, o que contraria a API-R04. A elegibilidade decide quem entra em uma experiência, não quem
pode deixá-la.

INV-01 diz que só contas elegíveis participam, e a saída não é participação: ela encerra a participação, sem dar acesso
a jogo, conteúdo ou outra pessoa.

Há um precedente contrário. Em API-R10 (sair da voz), o ADR-0028 e a spec (`spec.md:584`) dizem que o "sempre permitido"
não dispensa a policy. As duas saídas diferem nisto: a saída da sala é o único caminho da pessoa para deixar a sala e
liberar a reserva, enquanto a da voz tem substitutos no servidor (fim da sala, queda da conexão de hub com fail-closed).
Este ADR não muda a API-R10; se a voz deve seguir a mesma regra fica para quando ela for implementada.

Alternativas consideradas:

- **Manter a política e fechar a sala quando a conta muda de estado.** A suspensão já faz isso. Exigiria achar cada
  transição de estado, presente e futura, e uma esquecida volta a prender a pessoa.
- **Política nova, nomeada, só para a saída.** Daria a mesma exigência da fallback com mais uma política para manter.
- **Tirar a política de todo o grupo de salas.** Abriria leitura e ações a quem não é elegível.

## Decisão

Vamos mapear a saída fora do grupo, sem política própria, para valer a fallback de participante: sessão, senha definitiva,
MFA e conta não privilegiada. Continua valendo:

- CSRF, sem exceção nova;
- `404` para quem não participa da sala, decidido em `LoadRoomAsync`;
- o portão da conta suspensa, que não muda: a suspensão já aborta a sala e libera as reservas na mesma transação.

Nenhuma outra rota muda, e a rota continua sendo `/exit`, embora a spec a chame de `/departures`.

## Consequências

Conta inelegível alcança uma única rota de participação, e só com efeito sobre a sala em que está. A política do grupo
deixa de cobrir essa rota, que passa a constar em `TitularRoutes`.

## Compliance

`RoomExitEligibilityTests` e `EligibleParticipantPolicyTests.EveryParticipationRouteRequiresTheEligibleParticipantPolicy`,
com `CsrfEveryRouteTests`, `EndpointAuthorizationTests` e `SuspendedAccountRoutesTests` enumerando as rotas.
