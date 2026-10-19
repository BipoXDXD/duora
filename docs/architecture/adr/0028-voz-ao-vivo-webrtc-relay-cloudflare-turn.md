# ADR-0028: Voz ao vivo na sala por WebRTC P2P com relay forçado na Cloudflare Realtime TURN

- **Status:** Accepted (2026-10-19), decisão do responsável pelo produto em 2026-10-19
- **Origem:** spec 1.1, 4.2, 6.3, 6.5, 9, 11.3, 11.5, 12.2, 13.4, 16.3 e 23.2;
  [ADR-0007](0007-signalr-transporta-invalidacao.md) (adendo de 2026-10-19),
  [ADR-0008](0008-postgresql-para-jobs-e-outbox.md), [ADR-0009](0009-uma-instancia-web-no-piloto.md),
  [ADR-0016](0016-sala-e-sessao-de-jogo.md), [ADR-0027](0027-midia-das-experiencias-cenario-audio-e-3d.md);
  TM-68 a TM-79 de [threat-model.md](../../security/threat-model.md#512-voz-ao-vivo-na-sala); pesquisa de
  arquitetura de voz ao vivo de 2026-10-19 (fora do repositório); issue #86 (orçamento de CPU)
- **Revisar quando:** (1) o produto exigir mute imposto pelo servidor; (2) a sala passar a ter mais de 2
  pessoas; (3) o relay se mostrar instável no Safari. Nos três casos, o plano B é o LiveKit Cloud.

## Contexto

O responsável decidiu que as duas pessoas de uma sala podem conversar por voz dentro da experiência. Voz é a
superfície mais sensível do produto: identifica a pessoa, pode ser dado biométrico e expõe a assédio (*The Game
Audio Strategy Guide*, cap. 12, p. 383, sobre hostilidade contra jogadoras reconhecidas pela voz). As regras de
produto são fixas: a voz só existe se as duas pessoas entrarem nela; microfone desligado por padrão e ligado por
gesto; permissão do navegador pedida só nessa hora; indicador de microfone aberto e de quem fala; silenciar a
outra pessoa, desligar o próprio microfone e sair da voz sem sair da experiência; bloquear e denunciar a partir da
tela de voz; experiência completável sem voz; nada gravado, transcrito ou enviado a LLM; denúncia sem áudio; o IP
de uma pessoa nunca chega à outra.

Restrições de operação: uma VPS Hostinger KVM 2 (2 vCPU, 8 GB) com DNS na Cloudflare, uma instância web
(ADR-0009) e nenhum serviço novo sem necessidade (ADR-0015).

Alternativas consideradas:

- **WebRTC P2P com `iceTransportPolicy: "relay"` nos dois lados e Cloudflare Realtime TURN** (escolhida). A
  mídia vai de navegador a navegador, cifrada com DTLS-SRTP, sempre pelo relay da Cloudflare. Nada de mídia passa
  pela VPS. Contra: a Cloudflare vira sub-processador de IP e metadados; sem servidor de mídia, mute e expulsão
  não são impostos no fluxo de áudio.
- **LiveKit Cloud (SFU gerenciado).** Mute pelo servidor, expulsão imediata e salas com mais de 2 pessoas.
  Contra: fornecedor no caminho da mídia (decifra no SFU), custo por minuto e SDK maior. **Plano B**, pelos
  gatilhos de revisão acima.
- **coturn self-hosted na VPS.** Sem terceiro. Contra: portas UDP/TCP abertas na internet, banda e CPU de uma VPS
  de 2 vCPU, e operação de um relay exposto. Fica só como harness de teste.
- **LiveKit self-hosted.** Contra: o mesmo custo de CPU e de exposição do coturn, mais um serviço a operar.
- **P2P direto (sem relay).** Proibido: expõe o IP da outra pessoa.

## Decisão

Vamos implementar a voz ao vivo como WebRTC ponto a ponto entre os dois navegadores, com
`iceTransportPolicy: "relay"` nos dois lados e a Cloudflare Realtime TURN como único relay.

### Consentimento para ouvir e para falar

- Dois consentimentos separados. **Entrar na voz** (ouvir): a `RTCPeerConnection` só nasce quando as **duas**
  pessoas tocaram "Entrar na voz"; até lá ninguém ouve ninguém, e ninguém recebe credencial nem sinalização da
  outra pessoa. **Ligar o microfone** (falar): gesto separado, que pede a permissão do navegador só nessa hora.
- Sair da voz, a qualquer momento, desfaz o primeiro consentimento; desligar o microfone, o segundo.
- **Onde mora "entrou na voz":** no banco, em `experiences.voice_participation` (dono: Experiences), **uma linha
  por entrada** (`game_session_id`, `user_id`, `joined_at`, `left_at`), com índice único parcial em
  `(game_session_id, user_id) WHERE left_at IS NULL`: no máximo uma participação aberta por pessoa e sala, e o
  histórico de entradas e saídas fica para a denúncia (TM-73). Não fica em memória: precisa sobreviver a restart
  (ADR-0009, instância única) e é a base para emitir credencial. "Entrar na voz" é o comando `JoinVoice`
  (API-R09) e "Sair da voz" é `LeaveVoice` (API-R10): esse commit, que grava `left_at`, é o evento 1 da lista de
  revogação. O snapshot da sala (API-R02) diz se a própria pessoa e a outra estão na voz.
- **Reconciliação depois de restart: o servidor fecha a voz.** No start do processo web, `LeaveVoice` é aplicado
  a toda participação aberta, com a revogação pela outbox. Quando a conexão de hub de uma pessoa cai e não volta
  dentro do prazo do fail-closed, o servidor faz o mesmo com a participação dela. A voz só volta com novo
  "Entrar na voz"; nenhuma participação antiga é retomada.
- **API-R09 (`JoinVoice`):** policy `EligibleParticipant`; sala alheia ou inexistente → `404`; sem corpo
  (corpo ou campo extra → `400`); exige a sala `Active` e consulta `CheckInteraction` do Safety (par bloqueado →
  a mesma recusa do bloqueio); idempotente: com participação aberta, devolve o mesmo resultado sem linha nova;
  responde `204`.
- **API-R10 (`LeaveVoice`):** policy `EligibleParticipant` (o "sempre permitido" não dispensa a policy); sem
  corpo; responde `204` em qualquer caso, inclusive sala alheia ou pessoa já fora da voz, para não virar
  oráculo; um segundo DELETE não gera nova intenção na outbox.

### Autorização

- **API-R08** e o método do hub **`SendVoiceSignal`** exigem a policy `EligibleParticipant`
  (`backend/src/Duora.Web/Security/ParticipationPolicies.cs`) **e** participação ativa naquela sala. O método do
  hub tem autorização e teste próprios: não herda a autorização da conexão.

### Credenciais TURN

- Emitidas por **`POST /api/v1/rooms/{roomId}/voice/credentials`** (API-R08), só para quem entrou na voz;
  terceiro e participante de outra sala recebem `404`, igual a sala inexistente (ADR-0016).
- Antiforgery e `Origin` validados, rate limit por conta e por sala, resposta com `Cache-Control: no-store`.
  Credencial nunca em URL, log ou telemetria.
- **TTL de cerca de 5 min**, nunca além do fim da sessão de jogo, com renovação pelo mesmo endpoint, que
  revalida a participação a cada vez.
- Todo username emitido, inclusive nas renovações, fica em **`experiences.voice_credentials`** (dono:
  módulo Experiences, que é dono da sala; [modules.md](../modules.md)), com sala, pessoa, emissão, expiração e
  revogação. É a lista que a revogação percorre.
- A emissão é síncrona no web (chama a API da Cloudflare), com timeout e circuit breaker (spec 16.4); falha vira
  "voz indisponível", e a experiência segue pelo texto.
- **Revogação.** Os eventos que encerram a voz são os mesmos em toda a documentação (spec 6.5, API-R08, TM-72),
  e o commit de cada um é de quem é dono dele:
  1. a pessoa sai da voz ou sai da sala;
  2. bloqueio, em qualquer direção;
  3. denúncia feita a partir da sala ou da tela de voz;
  4. sala abortada (`Aborted`, spec 4.2), por desativação da versão ou pela administração (spec 6.3);
  5. medida de moderação que restringe a conta (spec 12.3), inclusive por suspeita de menor (spec 12.2);
  6. revogação da sessão de login (por `RevokeVoiceForUser`, que derruba de propósito a voz da conta em todos os
     aparelhos);
  7. fim da sessão de jogo.

  **Operações públicas do Experiences** ([modules.md](../modules.md)), as únicas que escrevem em
  `voice_participation` e `voice_credentials`: `JoinVoice`, `LeaveVoice`, `IssueVoiceCredential`,
  `RevokeVoiceForRoom(roomId)`, `RevokeVoiceForUser(userId)`, `RevokeAllVoice()` e
  `MarkVoiceCredentialsRevoked`. Os eventos 1, 4 e 7 são commits do próprio Experiences. Bloqueio, denúncia e
  medida de moderação (2, 3 e 5) são commits do Safety, e a revogação da sessão de login (6) é do Identity:
  nenhum deles escreve nas tabelas da voz. O coordenador do caso de uso chama `RevokeVoiceForRoom` ou
  `RevokeVoiceForUser` **na mesma transação** do evento (um `DbContext`, ADR-0004). A operação fecha a
  participação (`left_at`) e grava na outbox (ADR-0008) a intenção de revogar os usernames ativos. O worker
  chama a Cloudflare com retry limitado e, só depois da resposta, grava `revoked_at` por
  `MarkVoiceCredentialsRevoked`, que é código do Experiences rodando no worker. Credencial inexistente ou já
  vencida na Cloudflare conta como sucesso: o estado final desejado é o mesmo. Locks na ordem global de
  [modules.md](../modules.md#ordem-global-de-locks-spec-104): guardas e coordenador da sessão primeiro, depois
  `voice_participation` e `voice_credentials`. Se a revogação derruba a alocação já
  aberta é comportamento a confirmar num **teste de contrato manual contra a Cloudflare real**, que faz parte do
  gate de lançamento (spec 23.2); o TTL de 5 min limita a janela se não derrubar.

### Sinalização

- Pelo hub `/hubs/updates`, no método específico `SendVoiceSignal`, nunca genérico, entregue só ao outro
  participante. É uma exceção delimitada ao ADR-0007 (adendo de 2026-10-19): o hub carrega oferta, resposta e
  candidatos, dados de transporte efêmeros que não são persistidos.
- DTO estrito (tipo, `sdp` ou `candidate`, id da sessão de voz), campo desconhecido rejeitado, teto de cerca de
  16 KiB por mensagem e **rate limit por conexão**.
- O servidor rejeita candidato que não seja `typ relay`, inclusive as linhas `a=candidate` dentro do SDP, e
  rejeita (ou normaliza para `0.0.0.0`/`0`) `raddr`/`rport` diferentes de `0.0.0.0`, que podem carregar o IP
  público (mapeado) de quem enviou. **Esse filtro só pega erro de cliente honesto.** Quem protege a vítima é o próprio
  navegador dela, com as verificações abaixo; um cliente modificado do outro lado não consegue forçar um caminho
  direto se a vítima só aceita relay.
- O servidor repassa o SDP sem mexer na fingerprint DTLS (`a=fingerprint`).

### Verificações no cliente (proteção do IP de quem usa)

1. Antes de criar a oferta, confere `getConfiguration().iceTransportPolicy === "relay"`; se não for, não
   conecta.
2. Descarta todo candidato remoto que não seja `typ relay`.
3. Depois de conectar, lê `getStats()`: se o par de candidatos selecionado não for relay/relay, fecha a conexão.

### Controles

- **Silenciar a outra pessoa** ("mute soft"): acontece no cliente de quem ouve, que corta o áudio recebido. **A
  outra pessoa não é avisada.** Desligar o próprio microfone para a captura (`track.stop()`), não só zera o volume.
- **Bloquear** fecha a `RTCPeerConnection` no cliente de quem bloqueia na hora, sem esperar o servidor; depois
  vêm a revogação e o evento no hub para o outro lado.
- **Encerramento forçado** (eventos 2 a 7 da lista de revogação): revogação das credenciais mais um evento no hub
  que manda os dois clientes fecharem a conexão. Um cliente modificado pode ignorar o evento; a revogação no
  relay e o TTL curto são o que cortam o caminho.
- **Fail-closed:** sem conexão com o hub por mais de N segundos (15 s, hipótese), o cliente encerra a voz e
  avisa; volta só com novo "Entrar na voz".
- **Suspeita de menor:** a denúncia pela tela de voz inclui a categoria "suspeita de menor", que restringe o
  acesso da conta e revoga a voz na hora (spec 12.2). A maioridade por jurisdição é a lacuna J-05 do PR #83.
- **Kill switch:** gatilho operacional à parte, fora da lista de 7 eventos. Uma flag de Operations desliga a voz
  no produto inteiro: o botão some, API-R08 e API-R09 respondem indisponível, e `RevokeAllVoice` encerra as
  participações e revoga as credenciais abertas pela mesma outbox.
- Push-to-talk opcional; volume da voz da outra pessoa separado dos canais do catálogo; ducking da trilha enquanto
  alguém fala (ADR-0027).

### Garantias e limites

- **Sem gravação pelo Duora, contra uma VPS honesta:** a mídia é cifrada com DTLS-SRTP de navegador a navegador,
  e nem a VPS nem o relay têm o áudio em claro, **desde que a VPS repasse o SDP sem adulterar**. Uma VPS
  comprometida pode trocar a fingerprint DTLS no SDP e se colocar no meio; isso é ameaça registrada (TM-78), não
  garantia. O Duora não grava, não transcreve e não envia áudio a LLM nem a moderação; a denúncia não leva áudio
  (mudar isso exige ADR).
- **Aviso na tela de voz:** "A outra pessoa pode gravar o que você diz no próprio aparelho." O Duora não tem como
  impedir gravação local.
- **Privacidade:** a Cloudflare entra como sub-processador de IP e metadados de conexão (horário, duração, volume
  de tráfego), registrada em [data-map.md](../../privacy/data-map.md#fornecedores). A base legal depende do PR #83.
- **Custo:** alerta de gasto e de GB transferidos na conta da Cloudflare; o kill switch é a resposta. Risco
  residual aceito: quem tem uma credencial válida pode usar o relay como proxy UDP até ela vencer (5 min) ou ser
  revogada.

### Mudanças de infraestrutura (PR separado)

- `Permissions-Policy` passa de `microphone=()` para `microphone=(self)` no Caddy, mantendo `camera=()` e as
  demais restrições, com teste que confere o cabeçalho.
- `media-src 'self'` na CSP para os áudios do catálogo (ADR-0027). TURN não é governado por `connect-src`; a
  credencial vem da mesma origem.

### Orçamento de CPU

A VPS é uma KVM 2 com **2 vCPU**, mas a spec (15.1 e 23.2) ainda supõe 4 vCPU, e os limites `cpus` dos
serviços permanentes de `infra/compose/compose.prod.yml` somam 6,5 (postgres 2, backup 1, web 2, worker 1,
edge 0,5). Lacuna registrada na **issue #86**: cada `cpus` deve ser ≤ 2, a distribuição precisa ser revista e a
spec, corrigida. A voz não põe mídia na VPS: o custo nela
é emitir credencial e repassar sinalização.

Porquê técnico: é o caminho com menos componentes que atende a regra do IP e mantém a mídia cifrada entre os
navegadores, sem CPU nem portas novas numa VPS de 2 vCPU. Porquê de negócio: voz opcional e segura sem operar um
servidor de mídia; o plano B fica pronto para quando o produto pedir controle no servidor.

## Consequências

- Positivas: o IP do par nunca aparece para quem usa um navegador honesto; nenhuma mídia na VPS; relay e
  criptografia vêm prontos.
- Negativas: silenciar e encerrar dependem do cliente, mais a revogação e o TTL; a sala fica limitada a 2 pessoas;
  a Cloudflare entra no mapa de dados; o Safari tem histórico de problemas com relay
  ([bug 312332 do WebKit](https://bugs.webkit.org/show_bug.cgi?id=312332), número vindo da pesquisa, a confirmar),
  e por isso há teste manual no iOS.
- Trade-off: menos controle no servidor em troca de privacidade da mídia e operação simples.

## Segurança e operação

- STRIDE em [threat-model.md](../../security/threat-model.md#512-voz-ao-vivo-na-sala), TM-68 a TM-79.
- Telemetria só com contagem, duração e falha por motivo; sem IP, SDP, candidato nem credencial.
- Segredo da API da Cloudflare só no ambiente do web e do worker, com o app recusando subir sem ele quando a voz
  estiver ligada (spec 15.4).
- Indisponibilidade da Cloudflare desliga só a voz; a experiência continua pelo texto.

## Compliance

Testes previstos (a criar com a implementação; nenhum existe hoje):

- **Participação (API-R09, API-R10):** `JoinVoice` e `LeaveVoice` persistem em `voice_participation`, uma
  linha por entrada; R09 com sala alheia → `404`, com corpo → `400`, sala não `Active` ou par bloqueado →
  recusa, repetido → mesmo `204` sem linha nova; R10 → `204` em todos os casos, e o segundo DELETE não grava
  outra intenção na outbox; `LeaveVoice` grava a revogação na outbox no mesmo commit;
  `OpenVoiceParticipationsAreClosedOnWebStartup`; hub perdido além do prazo fecha a participação no servidor; nenhum módulo além do Experiences escreve nas tabelas da
  voz (teste de arquitetura sobre o mapeamento do EF Core).
- **Endpoint (API-R08):** participante que entrou na voz recebe credencial; terceiro e participante de outra sala
  recebem `404` sem dado da sala; conta sem `EligibleParticipant` recebe a recusa da policy; sem antiforgery ou
  com origem indevida → `403` `CSRF_VALIDATION_FAILED`; acima do rate limit → `429` com `Retry-After`; resposta
  com `no-store`; TTL nunca passa de ~5 min nem do fim da sessão; todo username fica em `voice_credentials`.
- **Hub:** `SendVoiceSignal` com autorização própria (`EligibleParticipant` e participação) e só entre os dois
  participantes; DTO com campo extra, acima de 16 KiB, acima do rate limit por conexão ou com candidato não
  `typ relay` (inclusive dentro do SDP) é rejeitado; `VoiceSignalWithRelatedAddressIsRejected` (`raddr`
  diferente de `0.0.0.0`); `ServerForwardsSdpFingerprintUnchanged`; o teste que enumera métodos do hub aceita
  esse método e nenhum genérico.
- **Revogação:** cada um dos 7 eventos chama `RevokeVoiceForRoom` ou `RevokeVoiceForUser` na transação do
  evento e grava a revogação na outbox no mesmo commit; o worker revoga com retry limitado e só então grava
  `revoked_at`; o kill switch (`RevokeAllVoice`) encerra participações abertas e recusa API-R08 e API-R09.
- **Adaptador da Cloudflare** contra servidor HTTP falso: emissão, revogação, timeout e erro. Nunca a
  Cloudflare real no CI; contra ela, só o teste de contrato manual do gate 23.2.
- **E2E no CI com coturn local via Testcontainers** e dois navegadores (Chromium e WebKit):
  `NoPeerConnectionUntilBothJoinVoice`; a chamada conecta só por relay e os candidatos remotos são todos `relay`,
  com `raddr` `0.0.0.0`; `ClientClosesVoiceWhenSelectedPairIsNotRelay`; o microfone só é pedido depois do gesto;
  desligar o microfone para a captura; bloquear derruba a voz dos dois lados; sem hub além de N segundos, a voz
  encerra.
- **Arquitetura:** nenhum módulo referencia STT, gravação ou LLM para a voz.
- **Infra (PR separado):** `Permissions-Policy` com `microphone=(self)` e `camera=()`.
- **Manual, no protocolo A11Y-04:** Safari no iOS com relay (bug 312332 do WebKit, número a confirmar), VoiceOver e teclado em todos
  os controles da voz.
