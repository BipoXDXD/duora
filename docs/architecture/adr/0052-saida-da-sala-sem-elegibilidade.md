# ADR-0052: Saída da sala sem exigir elegibilidade

- **Status:** Proposed (2026-11-09). Toca autorização: o aceite vem com a revisão humana no merge.
- **Origem:** API-R04, "Sempre permitir saída segura; liberar reserva" (`spec.md:578`); exceção ao ADR-0024, que aplica
  `RequireEligibleParticipant()` no grupo de participação
- **Revisar quando:** outro estado de conta passar a existir com sala aberta, ou a saída ganhar efeito além de abortar e
  liberar

## Contexto

`POST /rooms/{roomId}/exit` herdava `EligibleParticipant` do grupo. Quem deixava de ser elegível com a sala aberta recebia
`403` e não conseguia sair, o que contraria a API-R04. A elegibilidade decide quem entra em uma experiência, não quem
pode deixá-la.

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
