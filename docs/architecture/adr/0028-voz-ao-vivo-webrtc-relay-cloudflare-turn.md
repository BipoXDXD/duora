# ADR-0028: Voz ao vivo na sala por WebRTC P2P com relay forçado na Cloudflare Realtime TURN

- **Status:** Accepted (2026-10-19), decisão do responsável pelo produto em 2026-10-19
- **Origem:** spec 1.1, 6.5, 9, 11.3, 11.5, 13.4, 16.3 e 23.2;
  [ADR-0007](0007-signalr-transporta-invalidacao.md) (adendo de 2026-10-19),
  [ADR-0008](0008-postgresql-para-jobs-e-outbox.md), [ADR-0009](0009-uma-instancia-web-no-piloto.md),
  [ADR-0016](0016-sala-e-sessao-de-jogo.md), [ADR-0027](0027-midia-das-experiencias-cenario-audio-e-3d.md);
  TM-68 a TM-76 de [threat-model.md](../../security/threat-model.md#512-voz-ao-vivo-na-sala); pesquisa de
  arquitetura de voz ao vivo de 2026-10-19 (fora do repositório)
- **Revisar quando:** (1) o produto exigir mute imposto pelo servidor; (2) a sala passar a ter mais de 2
  pessoas; (3) o relay se mostrar instável no Safari. Nos três casos, o plano B é o LiveKit Cloud.

## Contexto

O responsável decidiu que as duas pessoas de uma sala podem conversar por voz dentro da experiência. Voz é a
superfície mais sensível do produto: identifica a pessoa, pode ser dado biométrico e expõe a assédio (*The Game
Audio Strategy Guide*, cap. 12, p. 383, sobre hostilidade contra jogadoras reconhecidas pela voz). As regras de
produto são fixas: microfone desligado por padrão e ligado por gesto; permissão do navegador pedida só nessa
hora; indicador de microfone aberto e de quem fala; silenciar a outra pessoa, desligar o próprio microfone e sair
da voz sem sair da experiência; bloquear e denunciar a partir da tela de voz; experiência completável sem voz;
nada gravado, transcrito ou enviado a processamento externo; denúncia sem áudio; o IP de uma pessoa nunca chega à outra.

Restrições de operação: uma VPS Hostinger KVM 2 (2 vCPU, 8 GB) com DNS na Cloudflare, uma instância web
(ADR-0009) e nenhum serviço novo sem necessidade (ADR-0015).

Alternativas consideradas:

- **WebRTC P2P com `iceTransportPolicy: "relay"` nos dois lados e Cloudflare Realtime TURN** (escolhida). A
  mídia vai de navegador a navegador, cifrada com DTLS-SRTP, sempre pelo relay da Cloudflare, então nenhum lado
  vê o IP do outro. Nada de mídia passa pela VPS. Contra: a Cloudflare vira sub-processador de IP e metadados;
  sem servidor de mídia, mute e expulsão não são impostos no fluxo de áudio.
- **LiveKit Cloud (SFU gerenciado).** Mute pelo servidor, expulsão imediata e salas com mais de 2 pessoas.
  Contra: fornecedor no caminho da mídia (decifra no SFU), custo por minuto e SDK maior. **Plano B**, pelos
  gatilhos de revisão acima.
- **coturn self-hosted na VPS.** Sem terceiro. Contra: portas UDP/TCP abertas na internet, banda e CPU de uma VPS
  de 2 vCPU já reservada pelos limites do Compose, e operação de um relay exposto. Fica só como harness de teste.
- **LiveKit self-hosted.** Contra: o mesmo custo de CPU e de exposição do coturn, mais um serviço a operar.
- **P2P direto (sem relay).** Proibido: expõe o IP da outra pessoa.

## Decisão

Vamos implementar a voz ao vivo como WebRTC ponto a ponto entre os dois navegadores, com
`iceTransportPolicy: "relay"` nos dois lados e a Cloudflare Realtime TURN como único relay.

### Credenciais TURN

- Emitidas por **`POST /api/v1/rooms/{roomId}/voice/credentials`** (API-R08): só para participante ativo daquela
  sala; quem não é participante recebe `404`, igual a sala inexistente (ADR-0016).
- Antiforgery e `Origin` validados, rate limit por conta e por sala, resposta com `Cache-Control: no-store`.
  Credencial nunca em URL, log ou telemetria.
- TTL = tempo restante da sessão de jogo, com teto de cerca de 20 min; renovação pelo mesmo endpoint, que
  revalida a participação.
- A emissão é síncrona no web (chama a API da Cloudflare), com timeout e circuit
  breaker (spec 16.4); falha vira "voz indisponível", e a experiência segue pelo texto.
- **Revogação pela outbox** (ADR-0008) ao sair, bloquear, denunciar, kick ou fim da sessão: o mesmo commit que
  muda a sala grava a intenção, e o worker revoga a credencial na Cloudflare com retry limitado. Que a revogação
  encerra as alocações já abertas é comportamento a confirmar no teste de contrato do adaptador.

### Sinalização

- Pelo hub `/hubs/updates`, num método específico da sala (`SendVoiceSignal`), nunca genérico: autorizado como
  qualquer leitura da sala e entregue só ao outro participante. É uma exceção delimitada ao ADR-0007 (adendo de
  2026-10-19): o hub carrega a oferta, a resposta e os candidatos, que são dados de transporte efêmeros, não a
  verdade do domínio, e nada disso é persistido.
- DTO estrito (tipo, `sdp` ou `candidate`, id da sessão de voz), campo desconhecido rejeitado, teto de cerca de
  16 KiB por mensagem.
- O servidor **rejeita todo candidato que não seja `typ relay`**, inclusive as linhas `a=candidate` dentro do SDP;
  isso protege contra um cliente modificado que tente vazar o próprio IP ou induzir conexão direta.

### Controles

- **Mute "soft":** silenciar a outra pessoa acontece no cliente de quem ouve (o áudio recebido é cortado lá).
  Desligar o próprio microfone para a captura (`track.stop()`), não só zera o volume.
- **Kick:** revogação da credencial mais um evento no hub que manda os dois clientes fecharem a conexão. Um
  cliente modificado pode ignorar o evento; a revogação no relay é o que corta o caminho.
- Push-to-talk opcional; volume da voz da outra pessoa separado dos canais do catálogo; ducking da trilha enquanto
  alguém fala (ADR-0027).

### Garantias e limites

- **Sem gravação pelo Duora:** a mídia é cifrada com DTLS-SRTP de navegador a navegador; nem a VPS nem o relay
  têm o áudio em claro. O Duora não grava, não transcreve e não envia áudio a processamento externo nem a moderação; a denúncia não
  leva áudio (mudar isso exige ADR).
- **Aviso na tela de voz:** "A outra pessoa pode gravar o que você diz no próprio aparelho." O Duora não tem como
  impedir gravação local.
- **Privacidade:** a Cloudflare entra como sub-processador de IP e metadados de conexão (horário, duração,
  volume de tráfego). Mapa de dados e base legal entram depois do PR #83 (público, idiomas e região).

### Mudanças de infraestrutura (PR separado)

- `Permissions-Policy: microphone=(self)` no Caddy.
- `media-src 'self'` na CSP para os áudios do catálogo (ADR-0027). TURN não é governado por `connect-src`; a
  credencial vem da mesma origem.

### Orçamento de CPU

A VPS é uma KVM 2 com **2 vCPU**, mas a spec (15.1 e 23.2) ainda supõe 4 vCPU, e os limites `cpus` dos
serviços permanentes de `infra/compose/compose.prod.yml` somam 6,5 (postgres 2, backup 1, web 2, worker 1,
edge 0,5). Limite de CPU no Compose é teto, não reserva, então a soma acima dos núcleos reais não quebra nada,
mas a divergência fica registrada como **lacuna**. A correção da spec fica para depois do PR #83, que reescreve
essas seções. A voz não põe mídia na VPS: o custo nela é emitir credencial e repassar sinalização.

Porquê técnico: é o caminho com menos componentes que atende a regra do IP e mantém a mídia cifrada de ponta a
ponta, sem CPU nem portas novas numa VPS de 2 vCPU. Porquê de negócio: voz opcional e segura sem operar um
servidor de mídia; o plano B fica pronto para quando o produto pedir controle no servidor.

## Consequências

- Positivas: o IP do par nunca aparece; nenhuma mídia na VPS; o relay e a criptografia vêm prontos.
- Negativas: mute e kick dependem do cliente, mais a revogação; a sala fica limitada a 2 pessoas; a Cloudflare
  entra no mapa de dados; o Safari tem histórico de problemas com relay (bug 312332 do WebKit, citado pela
  pesquisa), e por isso há teste manual no iOS.
- Trade-off: menos controle no servidor em troca de privacidade da mídia e operação simples.

## Segurança e operação

- STRIDE em [threat-model.md](../../security/threat-model.md#512-voz-ao-vivo-na-sala), TM-68 a TM-76.
- Telemetria só com contagem, duração e falha por motivo; sem IP, SDP, candidato nem credencial.
- Segredo da API da Cloudflare só no ambiente do web e do worker, com o app recusando subir sem ele quando a voz
  estiver ligada (spec 15.4).
- Indisponibilidade da Cloudflare desliga só a voz; a experiência continua pelo texto.

## Compliance

Testes previstos (a criar com a implementação; nenhum existe hoje):

- Integração do endpoint (API-R08): participante recebe credencial; terceiro e participante de outra sala
  recebem `404` sem dado da sala; sem antiforgery ou com origem indevida → `403` `CSRF_VALIDATION_FAILED`; acima do rate limit → `429` com `Retry-After`;
  resposta com `no-store`; TTL nunca passa do fim da sessão nem de ~20 min.
- Adaptador da Cloudflare contra servidor HTTP falso: emissão, revogação, timeout e erro (nunca a Cloudflare
  real no CI).
- Outbox: sair, bloquear, denunciar, kick e fim da sessão gravam a revogação no mesmo commit; o worker revoga
  com retry limitado.
- Hub: `SendVoiceSignal` só entre os dois participantes; DTO com campo extra, acima de 16 KiB ou com candidato
  não `typ relay` (inclusive dentro do SDP) é rejeitado; o teste que enumera métodos do hub aceita esse método
  e nenhum genérico.
- E2E no CI com **coturn local via Testcontainers** e dois navegadores (Chromium e WebKit): a chamada conecta só
  por relay, os candidatos remotos são todos `relay`, o microfone só é pedido depois do gesto, desligar o
  microfone para a captura, bloquear derruba a voz dos dois lados.
- Arquitetura: nenhum módulo referencia STT, gravação ou processamento externo para a voz.
- Manual, no protocolo A11Y-04: Safari no iOS com relay (por causa do bug 312332 do WebKit), VoiceOver e
  teclado em todos os controles da voz.
