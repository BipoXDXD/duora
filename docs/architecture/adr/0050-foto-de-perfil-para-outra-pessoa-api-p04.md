# ADR-0050: Foto de perfil vista por outra pessoa (API-P04): momento, contexto, faixa etária e escopo

- **Status:** Proposed (05/11/2026). **Nenhuma decisão foi tomada.** As cinco escolhas abaixo são do dono do
  projeto; as recomendações são propostas, não decisões. Autorização, migração e contrato exigem revisão humana
  antes do merge (AGENTS.md), e aceitar este ADR não aceita o contrato nem a migração que o implementem.
- **Origem:** [issue #173](https://github.com/BipoXDXD/duora/issues/173); [ADR-0035](0035-revisao-humana-de-fotos.md)
  (decisões de 28/10/2026 e nota de 05/11/2026, que deixam a API-P04 em aberto); spec 8.2 (API-P04), 1.1 (linha 38)
  e 12.1 (linha 950); inventário da #173 de 05/11/2026 (adendo do registro local de decisões pendentes, fora do
  repositório).
- **Revisar quando:** o dono decidir D1 a D5; o módulo Connections for integrado (novo contexto de leitura); a
  política de privacidade ou a decisão sobre idade mudar; ou uma issue própria do P04 for aberta.
- **Nota de 06/11/2026:** o dono aceitou as recomendações de D1 a D5. O status continua `Proposed`; ver
  [Escolhas do dono](#nota-de-06112026-escolhas-do-dono).

## Legenda

**[C]** confirmado por leitura da fonte citada, no `origin/main` `ebfec33d` (05/11/2026). Nada foi executado: nem
build, nem teste, nem Docker. **[I]** inferência de quem redigiu este ADR; não está escrita em nenhuma fonte e o
dono pode descartá-la.

## Contexto

A revisão humana de fotos (ADR-0035) já existe: o titular envia, o dono revisa como `moderator`, a foto aprovada
vira asset e só o titular a lê (API-M04). **Nenhuma outra pessoa vê a foto de ninguém.** A API-P04
(`GET /profiles/{userId}`, "projeção autorizada", "apenas contexto legítimo de sala/conexão; sem diretório
global", spec 8.2) é o que permitiria isso. O upload continua desligado em Production (`duora_media_review: ""`,
#69) [C, ADR-0035], então nada disto toca foto real em produção até a #69.

### O que falta [C]

| Peça | Estado em `ebfec33d` | Fonte |
|---|---|---|
| Rota `GET /profiles/{userId}` | Não existe. A Web só tem `/me/profile` (GET e PATCH) | `ProfileEndpoints.cs`; busca por `profiles/{` em `backend/src` e `frontend/src` sem resultado |
| Tabela `profiles.profile_photos` | Só em docs; sem migração nem código | `model.md`, `modules.md`; busca por `profile_photos` em `backend` e `frontend/src` sem resultado |
| Comando que seleciona uma foto aprovada para o perfil | Sem rota na spec: API-P02 só lista "campos permitidos" e M01 a M04 não selecionam. "Publicar asset não seleciona implicitamente foto do perfil" | spec 8.2; `media-review.md` |
| API-M04 para quem não é o titular | Só o titular lê: "ainda não há seleção de foto de perfil nem contexto de sala" | `media-review.md` (recorte API-M04) |
| Contexto de conexão | Sem `social.connections` em `backend/src`; "módulo Connections ainda não integrado" | ADR-0040; busca no código |
| Contexto de sala | Existe `game_sessions` e `GET /rooms/{roomId}` em Participation. **Estados na spec 4.2 e no ADR-0016:** `Invited`, `Ready`, `Active`, `Completed`, `Aborted`, `Expired`. **Estados no código:** o CHECK de `experiences.game_sessions.state` aceita só `Invited`, `Active`, `Completed`, `Aborted` e `Expired`, sem `Ready`; a segunda aceitação (bloco `count == 2` de `ParticipationService.Rooms.cs`, linhas 165 a 179 em `ebfec33d`) inicia o jogo, e o estado gravado vem de `SessionStatus` (`Active`, `Completed`, `Expired`) | spec 4.2; ADR-0016; `ParticipationSchema.cs:87`; `SessionStatus.cs`; `ParticipationService.Rooms.cs`. **Não li a projeção da sala e não afirmo o que ela devolve sobre a outra pessoa** |
| Faixa etária | Nenhum campo no código. `PATCH /me/profile` com `ageBand` responde 400 | `OwnProfileTests.UnknownOrRepeatedFieldIs400AndWritesNothing` |
| Testes de P04 | T-03 "faltam o ramo P04"; T-26 "faltam perfil bloqueado (API-P04, sem seleção de foto de perfil ainda) e o ledger externo" | `acceptance.md` |

Autorização das rotas que já existem (M01 a M04 e `/admin/photo-reviews`) tem teste HTTP por célula da matriz
[C, ADR-0035, nota de 05/11/2026, e `PhotoReviewApprovalTests`, classe parcial cujas células da matriz estão no arquivo `PhotoReviewAuthorizationMatrixTests.cs`]. O inventário de 05/11 listava essas
células como parciais; a `main` atual já as cobre. A revisão humana dessa autorização segue pendente na #173.

### Conflitos entre as fontes [C]

1. **Quando a foto aparece.** A spec 1.1 (linha 38) põe "fotos aprovadas" no perfil; a 12.1 (linha 950; era a 946
   no `1679a6f4` do inventário) lista o "Perfil exibido" como nome, faixa etária e região, sem foto; a API-P04 diz
   "sala/conexão" sem dizer em que estado. Pelo ADR-0016 a sala é a `game_session` e o "convite" é a sala em
   `Invited` (prazo de 60 s, spec 4.3). Nenhuma fonte diz em que ponto a foto passa a ser visível. [I] A ausência da
   foto na linha 950 pode ser só uma lista incompleta, não uma decisão.
2. **Faixa etária.** Spec linhas 38 e 950 e `model.md` (`profiles.age_band`, derivada de `birth_date`, D12) preveem
   uma faixa por pessoa. As decisões registradas dizem o contrário. A de 24/10/2026 (item 3) foi "decidida pelo assistente sob
   delegação expressa do titular"; em 29/10/2026 o dono decidiu direto (item 1, "Nada de idade"): "nenhuma data de
   nascimento, idade ou faixa etária é coletada" (ADR-0029; `data-map.md`, lacuna 3), e os campos etários que
   sobraram são "histórico técnico". O registro de 29/10 não cita uma ratificação explícita do item 3 de 24/10; diz
   que o que não está nele continua valendo. O próprio `model.md:309-310` ainda diz que `age_band` "não existe: a
   origem da faixa [...] está por decidir". O que existe por conta é a declaração "Declaro ter 18 anos ou mais", gravada
   no aceite dos Termos `2026-10-29`; ela é igual para todos e não é dado por pessoa.
   **Nota de 06/11/2026:** este conflito foi resolvido no texto, a favor das decisões (ver a nota da D3). O conflito 1
   continua: a linha 950 da spec segue sem foto.
3. **Texto da política de privacidade.** A versão `2026-10-25` diz que outra pessoa recebe "somente a projeção
   autorizada" e que "fotografias ... possuem etapas próprias"; não diz que a foto aprovada será mostrada à pessoa
   pareada. A decisão P4 de 29/10/2026 manteve o texto, e o aceite da política pelo titular segue aberto
   (ADR-0035). [I] Se o texto atual cobre essa exibição é pergunta de produto e jurídica; este ADR não a conclui.

### Restrições que qualquer opção respeita [C]

- INV-03 e INV-05; TM-24 (recurso indisponível e inexistente dão a mesma resposta 404) e TM-25 (revalida relação,
  bloqueio e estado do asset a cada leitura; `Cache-Control: no-store`).
- `concurrency.md` C2: a leitura (C03, P04, M04) não trava; o bloqueio é condição da própria consulta
  (`NOT EXISTS` em `safety.blocks`).
- Policy `EligibleParticipant` nas rotas de participante; conta privilegiada recebe 403 da política padrão nessas
  rotas; "uma rota nova nasce negada para a conta suspensa" (ADR-0042).
- Até 6 fotos por perfil (`profile_photos`, `CHECK position BETWEEN 1 AND 6`, spec 4.3).
- Quem escreve numa tabela é o dono; dado de outro módulo entra como projeção mínima (`modules.md`). Profiles é
  dono de `profile_photos` e Media é dono dos assets.

## Decisões abertas

Cada decisão é do dono. A **recomendação** de cada uma é só uma proposta; ela só vale depois que o dono a
escolher.

### D1. Quando a foto aparece

**Pergunta.** Em que ponto da jornada a pessoa passa a ver a foto aprovada da outra?

- **A. Desde o convite (sala `Invited`).**
  - Prós: o aceite é informado; a pessoa vê com quem vai jogar antes de se comprometer.
  - Contras: expõe a foto a quem talvez nunca aceite (o convite expira em 60 s) e a toda pessoa pareada na
    rodada, mesmo sem jogo; ao longo de um evento, os pares formados viram um catálogo de rostos [I]; a recusa de
    uma pessoa revela o rosto dela sem reciprocidade [I]; a revogação (bloqueio, saída, expiração) tem de valer
    numa janela curta; vai contra a ideia de o jogo ser "o mediador do encontro" (spec 13.1) [I].
- **B. Sala `Active` (depois das duas aceitações).** No código, a segunda aceitação leva `Invited` direto a
  `Active`; não há `Ready` (ver "O que falta").
  - Prós: reciprocidade: ninguém vê antes de os dois aceitarem; a exposição fica restrita a quem está jogando
    junto; deixa de valer sozinha quando a sala vira `Aborted` ou `Expired`; é compatível com "sala" na API-P04.
  - Contras: o aceite é dado sem ver a foto; exige decidir o que acontece em `Completed` (ver D1.1); cada leitura
    revalida o estado da sala.
- **C. Só na conexão (interesse mútuo, conexão ativa).**
  - Prós: menor exposição; nenhum rosto antes de interesse mútuo, coerente com "uma conversa persistente só nasce
    de interesse mútuo" (spec 1.1); ampliar depois é aditivo [I].
  - Contras: a decisão privada (API-R06) é tomada sem a foto e o jogo inteiro corre sem rosto, uma mudança de
    produto [I]; o módulo Connections não existe no código, então o P04 não teria nenhum contexto para entregar
    até ele existir.
- **D. Só em `Completed` (depois do jogo, durante a janela de decisão).**
  - Prós: o jogo corre sem rosto, o que preserva o jogo como mediador (spec 13.1) [I]; a foto aparece quando a
    pessoa vai decidir; só vê quem jogou até o fim.
  - Contras: quem sai antes, ou a sala `Aborted` ou `Expired`, nunca vê a foto; o acesso passa a depender da
    janela de 24 h (spec 4.3), então a D1.1 deixa de ser subpergunta e vira a própria regra; nenhuma fonte pede
    esse momento [I].
- **E. A pessoa escolhe mostrar (combinável com A, B, C ou D).** A foto só aparece se o titular tiver ligado a
  visibilidade, dentro do contexto máximo que a opção escolhida permitir. Profiles já é dono de "visibilidade"
  (`modules.md`).
  - Prós: controle do titular; a exposição padrão é a menor.
  - Contras: cria uma preferência de visibilidade por pessoa que a spec não define (contrato, tabela e testes
    novos) [I]; a pessoa que não mostra vê a outra mesmo assim? É preciso decidir a reciprocidade; nenhuma fonte
    responde.

**D1.1 (subpergunta, se a escolha for A, B ou D).** O acesso continua em `Completed` enquanto a janela de decisão
(24 h, spec 4.3) estiver aberta? Sem isso, a pessoa decide sem a foto que acabou de ver. Nenhuma fonte responde.

**Recomendação (não decidida): B (sala `Active`), com acesso em `Completed` só enquanto a janela de decisão estiver aberta [I], e
o contexto de conexão previsto no contrato como acréscimo futuro, quando o Connections existir.** Motivo:
reciprocidade sem esperar um módulo ausente, e alargar o contexto depois é aditivo, enquanto encolher uma
exposição já liberada não é. **Custo principal:** o aceite do convite continua sem foto.

### D2. Qual contexto autoriza a leitura

**Pergunta.** Como o servidor decide que a relação é legítima, e que relações contam? A lista de contextos
autorizadores sai da D1. Em qualquer opção valem, na leitura atual: chamador elegível, `CheckInteraction` sem
bloqueio nem restrição entre as duas contas, e foto aprovada, sem tombstone e escolhida no perfil [I: a condição
da foto escolhida vem de "Asset Approved não basta" em `media-review.md`].

- **A. Relação derivada no servidor, na rota da spec (`GET /profiles/{userId}`).** O servidor procura um contexto
  legítimo entre a conta da sessão e `userId`.
  - Prós: casa com a spec (sem id de contexto vindo do cliente, menos espaço para confused deputy); 404 uniforme
    simples.
  - Contras: com mais de um contexto (sala e conexão), a regra de qual vale precisa ser uma união explícita; o
    motivo da autorização não aparece na requisição, o que dificulta o diagnóstico [I]; uma consulta de relação por
    leitura (sem lock, `concurrency.md` C2).
- **B. Contexto explícito na requisição** (por exemplo `GET /rooms/{roomId}/participants/{userId}/profile`).
  - Prós: ancora a autorização num recurso que já responde 404 para quem não participa; o motivo fica explícito.
  - Contras: contradiz a rota e a coluna "Autorização" da API-P04, então muda a spec e o contrato; o cliente precisa
    conhecer o contexto; uma rota por contexto (sala, conexão).
- **C. Sem rota de perfil.** A projeção e o `assetId` saem dentro da projeção da sala (API-R02) e da conexão
  (API-C01); a API-M04 autoriza o byte pelo contexto.
  - Prós: não cria a rota que mais se parece com um diretório; segue o modelo de projeção por destinatário (INV-05).
  - Contras: a API-P04 deixa de existir como rota (contradiz a spec); acopla Experiences e Connections ao
    Profiles; a mesma regra de autorização passa a morar em vários lugares e pode divergir.

Em qualquer opção, [I] a API-P04 e a API-M04 devem usar **a mesma função de autorização**, e a API-M04 só serve a
terceiro um asset escolhido no perfil do dono, nunca qualquer asset aprovado. Hoje `GET /api/v1/media/{assetId:guid}`
está em `TitularRoutes` de `EligibleParticipantPolicyTests` [C], isto é, exige só sessão e não a policy
`EligibleParticipant`. Estendê-la a terceiros exige tirá-la dessa lista (o que também passa a exigir elegibilidade
do titular, uma mudança de comportamento) ou checar a elegibilidade dentro da função de autorização [I].

**Recomendação (não decidida): A, com uma única função de autorização compartilhada por P04 e M04, lista fechada
de contextos (a da D1) e 404 uniforme para sem relação, bloqueado, restrito ou inexistente.** Motivo: é a opção
que a spec já descreve e a de menor superfície. **Custo principal:** o motivo da autorização não aparece na
requisição; a matriz de testes da seção Compliance cobre o que a requisição não mostra.

### D3. Faixa etária no P04 e de onde vem o dado

**Pergunta.** A projeção do P04 mostra faixa etária? Se sim, de onde vem?

Hoje não há dado: `age_band` só existe em docs (`model.md:309-310`: "ainda não existe"), e as decisões registradas
são não coletar data de nascimento, idade nem faixa (conflito 2).

**Nota de 06/11/2026 (alinhamento de texto, sem decisão nova).** O dono mandou alinhar os docs às decisões já
registradas (24/10, item 3; 29/10, item 1, "Nada de idade"; ADR-0029; `data-map.md`, lacuna 3), sem reabri-las. A
faixa etária saiu de `spec.md` (linhas 38 e 950), `scope.md` (linhas 21 e 151) e `screens.md` (linha 151); em
`model.md`, `age_band` saiu do diagrama, das colunas e das constraints, e D12 e "Implementado" foram atualizados.
As citações a `model.md:309-310` e à redação antiga neste ADR são do `ebfec33d`. Efeitos nesta D3: o contra da
opção A sobre a emenda e o custo principal da recomendação deixam de valer; a opção C agora também contradiz o
texto da spec, além das decisões. O alinhamento não escolhe entre A e B, porque nenhuma decisão registrada trata
do rótulo fixo "18+". Esta nota não decide D1, D2, D4 nem D5, e o status deste ADR continua `Proposed`.

**Nota posterior de 06/11/2026:** o dono escolheu A, sem faixa e sem rótulo; ver
[Escolhas do dono](#nota-de-06112026-escolhas-do-dono).

- **A. Sem faixa etária no P04.**
  - Prós: coerente com as decisões de 24/10 e 29/10 e com o ADR-0029; nenhum dado novo; nada que se pareça com
    verificação.
  - Contras: contradiz o texto literal da spec (linhas 38 e 950) e do `model.md` (`age_band`, D12), que precisariam
    de emenda em PR de docs com aprovação do dono; o produto perde um atributo que a spec previa.
- **B. Rótulo fixo "18+", igual para todos, vindo do requisito dos Termos.**
  - Prós: comunica que o serviço é só para adultos; nenhum dado novo por pessoa.
  - Contras: não é fato verificado, é autodeclaração, e pode ser lido como verificação, o que o registro de
    29/10 evita ("nenhum documento deve afirmar que o Duora cumpre o ECA Digital"); contas anteriores à versão
    `2026-10-29` dos Termos podem não ter a declaração [I]; informa o mesmo a todos, então acrescenta pouco.
- **C. Faixa por pessoa (por exemplo `18-24`).**
  - Prós: cumpre o texto original da spec.
  - Contras: exige coletar data de nascimento ou faixa autodeclarada, o que reverte as decisões de 24/10 e 29/10
    e o ADR-0029; dado novo (`profiles.profiles` é classe P2 e a `birth_date` de origem é P3, `model.md` D12) e análise de idade por jurisdição ainda aberta (`data-map.md`,
    J-05); não é decisão que o P04 possa tomar sozinho.

**Recomendação (não decidida): A.** Motivo: é a única opção que não contradiz uma decisão registrada (24/10 sob delegação; 29/10 pelo dono).
Acrescentar o campo depois é aditivo para clientes tolerantes [I]. **Custo principal:** a spec e o `model.md`
ficam em desacordo com o código até uma emenda de docs ser aprovada.

### D4. P04 e a tela do titular fecham a #173 ou viram issue própria

**Pergunta.** A entrega do P04 (e da tela de envio do titular, que consome API-M01 a M03) encerra a #173, ou vira
issue própria?

A #173 hoje depende de coisas que o P04 não entrega: ledger externo de exclusões (custódia B2, #69),
aceite da política de privacidade pelo titular, revisão humana da autorização e ativação do upload (#69)
[C, ADR-0035, nota de 05/11/2026]. A issue já foi fechada duas vezes e reaberta (26/10 e 29/10), com a regra do
dono de que só fecha "depois de resolver as pendências listadas" [C, comentários da #173].

- **A. P04 e tela fecham a #173.** Exige o dono redefinir os critérios: mover o ledger, o aceite da política e a
  ativação para a #69.
  - Prós: uma issue a menos aberta; rastreabilidade em um lugar.
  - Contras: fechar a #173 muda o critério combinado, justamente o que o dono já recusou duas vezes [I]; o P04 ficaria
    atrás das mesmas travas da #69.
- **B. Issue própria para o P04 e a tela; a #173 fica com o que resta da revisão.**
  - Prós: cada issue fecha pelo critério dela; o P04 pode ser revisado sem esperar a #69; a #173 não é fechada
    por conveniência.
  - Contras: divide o histórico; é preciso carregar para a issue nova a linha "Perfil/asset aprovado" da matriz de
    aceite de `media-review.md` e atualizar os critérios da #173 para não perdê-la.

- **C. Adiar o P04 até a #69 e a D5 estarem resolvidas, sem issue nova agora.**
  - Prós: não migra nem publica contrato que ainda não pode ser exercitado (sem foto real em produção e sem
    seleção); nenhuma issue nova para manter.
  - Contras: a autorização com foto de ponta a ponta fica sem prova até lá; a #173 continua com a lacuna aberta e a
    spec com o conflito; é fácil esquecer o item [I].

**Recomendação (não decidida): B, e este ADR não fecha nem edita a #173.** Motivo: preserva o critério que o dono
já fixou; C é uma alternativa legítima se o dono preferir não manter uma issue que ainda não tem como exercitar a rota. **Custo principal:** manter duas issues ligadas até a #69.

### Pontos que dependem do dono, sem opções a comparar

- **Aceite do contrato e da migração.** A rota P04 e a tabela `profile_photos` são contrato e migração; cada PR de
  implementação exige revisão humana. Aceitar este ADR não os aceita.
- **Risco residual R (sem trilha de quem viu a foto).** Precisa de aceite explícito do dono, ou de uma decisão por
  trilha de leitura. Sem uma das duas, a linha R do STRIDE fica sem teste nem aceite.
- **Quem revisa a autorização.** AGENTS.md exige revisão humana; na #173 a revisão humana da autorização está
  pendente. [I] O dono é hoje o único revisor humano.

### D5. Como a foto é escolhida para o perfil (antes, lacuna G1)

**Pergunta.** Qual é o comando que grava `profile_photos` (escolha, ordem e remoção)? A spec não tem rota: API-P02 só
lista "campos permitidos", M01 a M04 não selecionam, e `media-review.md` diz que publicar um asset não o seleciona.
Sem esse comando, o P04 devolve lista vazia.

- **A. Recurso próprio, por exemplo `PUT /me/profile/photos`, com `If-Match` próprio.**
  - Prós: valida asset do chamador e `Approved` num ponto só; versão independente do texto do perfil, como as
    preferências (spec 8.2, API-P07 e P03) [I]; ordem e remoção no mesmo comando.
  - Contras: rota e contrato novos; mais um recurso versionado.
- **B. Estender `PATCH /me/profile` (API-P02) com o campo de fotos.**
  - Prós: nenhuma rota nova; já tem `If-Match`.
  - Contras: mistura texto e fotos na mesma versão; o PATCH hoje recusa campo desconhecido, então o campo é uma
    mudança de contrato do P02; a validação cruza com Media.
- **C. Seleção automática: publicar o asset o põe no perfil.**
  - Prós: nenhum comando novo.
  - Contras: contradiz `media-review.md` ("publicar asset não seleciona implicitamente foto do perfil"); sem
    controle de ordem nem de quais fotos aparecem.
- **D. Sem seleção nesta entrega.** O P04 devolve lista vazia até uma decisão posterior.
  - Prós: nada a decidir agora.
  - Contras: o P04 não mostra foto nenhuma, então não exercita a autorização que existe para proteger a foto.

**Recomendação (não decidida): A.** Motivo: isola a validação cruzada num comando só e não toca no contrato do P02.
**Custo principal:** uma rota e um recurso versionado a mais.

### Lacunas que a spec não resolve [I]

Estas lacunas vêm da leitura das fontes; não são requisitos que este ADR cria.

- **G2. Exclusão e rejeição × `profile_photos`.** A API-M03 marca a foto `Deleted` e grava tombstone. Falta dizer se
  a linha de `profile_photos` sai na mesma transação ou se a leitura só filtra pelo estado do asset. A leitura
  revalida de qualquer forma (TM-25).
- **G3. Concorrência.** `concurrency.md` não tem cenário para seleção de foto contra exclusão ou revisão, nem para
  a decisão acima.

## Decisão

Nenhuma. Este ADR passa a `Accepted` num PR posterior que registre as escolhas do dono em D1 a D5 e, se D3 for A,
emende a spec (linhas 38 e 950) e o `model.md` (`age_band`, D12) para refletir a decisão de 24/10 e 29/10.
**Nota de 06/11/2026:** essa emenda já foi feita (ver a nota da D3) e não depende mais deste ADR. O `Accepted`
continua exigindo as escolhas do dono.

| ID | Pergunta | Recomendação (não decidida) |
|---|---|---|
| D1 | Quando a foto aparece | B: sala `Active`, e em `Completed` só durante a janela de decisão; conexão como acréscimo futuro |
| D2 | Qual contexto autoriza | A: relação derivada no servidor, função única para P04 e M04, lista fechada, 404 uniforme |
| D3 | Faixa etária | A: sem faixa etária no P04 (`model.md:309-310` já diz que o campo não existe) |
| D4 | Escopo | B: issue própria; a #173 fica com o que resta |
| D5 | Como a foto é escolhida para o perfil | A: recurso próprio de seleção, com `If-Match` |

### Nota de 06/11/2026: escolhas do dono

Nota acrescentada; o texto acima não foi alterado. O dono aceitou as recomendações:

| ID | Escolha do dono |
|---|---|
| D1 | B: sala `Active`, mais as 24 h da janela de decisão em `Completed` (responde à D1.1); conexão como acréscimo futuro, quando o Connections existir |
| D2 | A: relação derivada no servidor, função única de autorização para P04 e M04, lista fechada de contextos e 404 uniforme |
| D3 | A: sem faixa etária e sem rótulo "18+" no P04 |
| D4 | B: issue própria para o P04 e a tela; a #173 fica com o que resta. A issue ainda não foi aberta |
| D5 | A: recurso próprio de seleção, com `If-Match` próprio |

**O ADR continua `Proposed`.** Registrar estas escolhas não basta para o `Accepted`, porque seguem com o dono:

- o aceite do contrato e da migração;
- o risco residual R (sem trilha de quem viu a foto);
- se a política de privacidade cobre a exibição da foto à pessoa pareada (conflito 3).

Os demais pontos de "Pontos que dependem do dono" não mudam. Esta nota não autoriza implementar a rota, migrar,
criar flag nem ligar o upload (#69).

## Consequências

Se o dono adotar as recomendações:

- **Positivas.** Ninguém vê foto antes de os dois aceitarem; nenhum dado pessoal novo; uma só regra de autorização
  para a projeção e para os bytes; a #173 mantém seu critério de fechamento.
- **Negativas.** O aceite do convite é dado sem foto; a spec e o `model.md` ficam em desacordo com o código até a
  emenda (**nota de 06/11/2026:** emenda feita, ver a D3); o P04 não tem utilidade enquanto a D5 não for resolvida e o upload estiver desligado (#69).
- **Trade-off.** Menor exposição em troca de uma experiência menos "visual" antes do jogo.

Se o dono escolher outra opção: A em D1 aumenta a superfície de exposição e de revogação; C em D1 deixa o P04 sem
contexto até o Connections existir; B em D2 exige emenda da spec; C em D3 reabre decisões já tomadas e a análise
por jurisdição.

## Segurança e operação

- **Sem ativação.** O P04 não liga upload, não cria flag e não muda a allowlist de Production; sem foto real
  (#69), a rota devolveria perfil sem foto. [I]
- **Privacidade.** Mostrar a foto à pessoa pareada é um tratamento novo de dado de classe P3; o texto da política
  e o aceite do titular seguem abertos (conflito 3). Resposta com `Cache-Control: no-store`, sem `ETag` que
  permita revalidar conteúdo de terceiro [I, por TM-25].
- **Banco.** `profile_photos` é migração aditiva (expand). Rollback: a rota nova some junto da versão e nenhum
  dado de revisão é tocado [I].
- **Logs e métricas.** Sem id de pessoa como label e sem foto, nome ou região em log (spec 12.1, telemetria).
- **Backup e restore.** Sem mudança própria: a foto segue a política de derivados e tombstones da ADR-0035. O
  restore não pode republicar foto excluída (T-26; o restore já falha fechado sem a barreira do ledger, `media-review.md`).

### STRIDE

Os testes abaixo **ainda não existem** (a rota não existe); os nomes são propostos e seguem o padrão dos testes
atuais. Cada ameaça tem de ter um teste que falhe sem a mitigação.

| Ameaça | Mitigação (sob as recomendações) | Teste proposto |
|---|---|---|
| S: ler o perfil de A com um `userId` adivinhado, sem relação | Relação derivada da sessão no servidor; nenhum id de contexto vindo do cliente | `AStrangerReadingAnotherProfileGetsTheSameNotFoundAsAMissingId` |
| S/I: ler o byte de uma foto de A pelo `assetId` | A API-M04 só serve a terceiro asset escolhido no perfil do dono e sob a mesma função de autorização. Relação, foto escolhida, estado do asset e bloqueio entram **na mesma consulta SQL**, não em passos separados, para a recusa não variar com o motivo (TM-24) | `AnApprovedAssetOutsideTheOwnersProfileOrWithoutRelationshipIsTheSameNotFound` e `EveryRefusalReasonRunsTheSameSqlCommands`, que compara os comandos SQL emitidos em cada motivo de recusa, como `BlockSideChannelTests.EveryTargetRunsTheSameCommands`. Não prova ausência de oráculo de tempo (risco residual do TM-24) |
| T: selecionar o asset de outra pessoa no próprio perfil (se a D5 entrar) | Asset precisa ser do chamador e `Approved`; DTO estrito, sem `ownerId` nem `status` | `SelectingSomeoneElsesAssetIsNotFoundAndWritesNothing` |
| T: `profile_photos` aponta para foto depois excluída ou rejeitada | Leitura revalida o estado do asset; G2 define a limpeza | `AfterTheOwnerDeletesThePhotoTheOtherPersonGetsNotFoundOnTheNextRead` |
| R: não há trilha de quem viu uma foto | Nenhuma mitigação proposta: nenhuma fonte exige trilha de leitura de foto por participante [I]. **Exige aceite explícito do dono do risco residual** (ver "Pontos que dependem do dono") | Sem teste enquanto o risco não for aceito. Se o dono pedir trilha: `ReadingAnotherPersonsPhotoIsAuditedInTheSameTransactionAsTheDecisionToServeIt` |
| I: 404 ou 403 vaza dado de A ou o motivo da recusa | Mesmo status e mesmo corpo para sem relação, bloqueado, restrito e inexistente | `RefusalBodiesCarryNoDataOfTheOwnerAndAreIdentical` (canário `CANARY-<uuid>` no nome de A) |
| I: a projeção traz campo a mais (e-mail, preferências, versão, idade) | DTO de saída com allowlist de campos | `ProfileProjectionHasExactlyTheAllowedKeys` |
| I: foto de A servida depois de bloqueio, exclusão, rejeição ou fim do contexto | Revalidação a cada leitura, `no-store` (TM-25) | `TheSameUrlAnswersNotFoundAfterABlockADeletionOrTheEndOfTheContext` |
| D: varredura de `userId` ou de `assetId` | 404 uniforme e limite de taxa por conta, com 429 e `Retry-After` [I: o valor do limite é detalhe de implementação] | `ProfileAndMediaReadsOfOthersAreRateLimitedPerAccount` |
| E: conta privilegiada (`moderator`, `product_admin`, `operator`) usa a rota de participante | Política padrão: 403 | `PrivilegedAccountsAreForbiddenOnTheParticipantProfileRoutes` |
| E: conta suspensa ou restrita lê, ou é lida | ADR-0042: a rota nasce negada para suspensa; `CheckInteraction` para restrição | `ASuspendedCallerIsForbiddenAndASuspendedCounterpartIsTheSameNotFound` |
| T/race: bloqueio simultâneo à leitura | Bloqueio é condição da consulta; leitura sem lock (C2) | `ABlockCommittedBeforeTheReadAlwaysDeniesIt` (T-03, ramo P04) |

## Compliance

**Autorização (exigida; os itens 1 a 7 supõem D2-A e dependem de D1 e D5).** Com D2-B, o item 1 passa a testar a rota
por contexto (B numa sala de A, ou fora dela, dá 404) e o item 5 muda a rota testada; com D2-C, os testes passam para
as projeções da sala e da conexão e para a API-M04. Para cada rota nova (P04 e M04 para terceiro, e a de seleção, se a D5 criar uma), com PostgreSQL
real e dados sintéticos, cada teste afirma o status **e** que o corpo não traz dado alheio, e confere que banco e
armazenamento não mudaram:

1. **Usuário B em recurso de A, sem relação:** `GET /profiles/{A}` e `GET /media/{assetDeA}` dão **404**, corpo sem
   nome, região, `assetId` nem chave de A, **idêntico** ao de um id inexistente, com `no-store`.
2. **B com relação, mas fora do contexto da D1** (convite, sala abortada, expirada ou `Completed` com a janela
   fechada, conforme o dono decidir): 404 uniforme.
3. **B com relação válida:** 200 com exatamente o conjunto de chaves esperado; bytes `image/webp`, `no-store`.
4. **Bloqueio** em qualquer direção, conta restrita ou suspensa de qualquer lado: 404 uniforme (T-03).
5. **Anônimo:** 401, por `EndpointAuthorizationTests.EveryOtherRouteAnswers401WithoutSession`, que enumera as rotas
   e já cobre rota nova. **Política de participação:** `EligibleParticipantPolicyTests` exige `EligibleParticipant` em
   toda rota de `/api/v1` fora de `TitularRoutes`; a rota P04 nasce fora da lista e é checada sozinha. A
   `GET /media/{assetId}` **está em `TitularRoutes`**: estendê-la a terceiros exige tirá-la da lista ou checar a
   elegibilidade dentro da função de autorização (D2). **Conta privilegiada:** 403. **Conta suspensa:**
   `ACCOUNT_SUSPENDED` (`SuspendedAccountRoutesTests`).
6. **Mutação de A por B (só se a D5 criar uma rota de escrita):** `PUT`, `PATCH` e `DELETE` de B no recurso de A dão 404 e o recurso de A
   fica intacto.
7. **URL antiga** depois de exclusão, rejeição ou bloqueio: 404 (T-26; estende `MediaAssetReadTests`).

**Contrato.** O gate `contract` do CI regenera o OpenAPI e os tipos; a rota é aditiva e `oasdiff` não deve apontar
breaking change (ADR-0018). Escolher B em D2 muda a rota da spec e exige aprovação explícita.

**Arquitetura.** Profiles lê o asset só pela operação pública de Media, sem tocar nas tabelas de Media
(`modules.md`). Não verifiquei se os testes de arquitetura atuais (`DependencyRulesTests` e vizinhos) já impõem essa
fronteira para Profiles e Media; se não impõem, a implementação deve estendê-los.

**Docs.** Ao implementar, atualizar as linhas T-03, T-05 e T-26 de `acceptance.md` e a matriz de `media-review.md`.

**Marco previsto:** M4/M5 (`media-review.md`: "HTTP/UI bilíngue, autorização e API-M03/M04/perfil"). Nada disto está implementado.

**Verificação desta proposta.** O PR é só documentação: nenhum build, teste ou Docker foi executado. O CI do PR
valida as suítes existentes contra a mudança documental; ele não prova nenhuma das decisões acima.
