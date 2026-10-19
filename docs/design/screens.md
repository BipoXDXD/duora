# Telas, estados e wireframes

> **Decisão vigente — 19/10/2026, issue #93:** verificação etária foi retirada. Participação exige conta Active, e-mail confirmado e selfie de qualidade/presença Passed, além dos demais controles. Referências etárias remanescentes descrevem o sistema anterior ou questões jurídicas históricas; não são requisitos de implementação. Ver [ADR-0029](../architecture/adr/0029-confirmacao-email-presenca-sem-idade.md).


Especificação de cada tela do mapa da [spec 13.2](../product/spec.md#132-mapa-de-telas-e-estados): propósito,
conteúdo, ações e estados obrigatórios, com wireframes mobile-first das telas centrais. Navegação e
recuperação estão em [information-architecture.md](information-architecture.md); tokens, ações e componentes,
em [system.md](system.md); critérios de acessibilidade, em [accessibility.md](accessibility.md).

> **Status:** wireframes de estrutura, em cinza, antes de cor e ilustração (spec 13.1, "Wireframes"; RUI cap. 1,
> "Hold the color"). Nenhum texto aqui é redação final aprovada.

## Convenções

- **Largura de referência:** 360 CSS px (cerca de 40 colunas nos desenhos), com gutter de 16 px. Toda tela
  também funciona a 320 px e com zoom de 200% (A11Y-06); em telas largas, o conteúdo fica numa coluna de
  leitura (`max-w-prose` ou `max-w-5xl`), sem esticar formulários (RUI cap. 3, "You don't have to fill the
  whole screen").
- **Uma ação primária por tarefa**, marcada nos desenhos como `[■ Rótulo ]`. Secundária: `[ Rótulo ]`.
  Neutra/link: `Rótulo ›`. Destrutiva só aparece em confirmações: `[▲ Rótulo ]`.
- **Estados transversais**, obrigatórios em toda tela que carrega dados (spec 13.2). São distintos e nunca se
  substituem:

| Estado | Quando | Apresentação | Nunca |
|---|---|---|---|
| `loading` | Primeira carga ou troca de rota | Skeleton com a forma do conteúdo; `aria-busy` na região; título da tela já visível | Spinner de página inteira por mais de 1 s sem texto |
| `empty` | Resposta bem-sucedida e sem itens | Texto que explica por quê + uma chamada para ação; UI de filtro oculta (RUI cap. 8) | Ser usado para erro ou falta de rede |
| `error` | Resposta com falha (4xx/5xx inesperado) | Mensagem em texto, o que fazer, "Tentar de novo"; foco no título do erro | Mostrar payload, código interno ou stack |
| `offline` | Navegador sem rede ou hub desconectado além da tolerância | Faixa "Sem conexão" no topo; conteúdo já carregado continua visível e marcado como possivelmente desatualizado | Apagar o que já estava na tela |

- **Sessão expirada** em qualquer tela autenticada leva ao login com aviso, conforme os
  [caminhos de recuperação](information-architecture.md#caminhos-de-recuperação).

## Apresentação pública

- **Propósito:** explicar o que é o Duora, como funciona um encontro, as regras de segurança e o compromisso
  de acessibilidade, antes de qualquer cadastro.
- **Conteúdo:** proposta em uma frase, "como funciona" em três passos (evento → experiência → decisão privada),
  regras de convivência, o que a outra pessoa vê, declaração de acessibilidade.
- **Ações:** primária "Criar conta"; secundária "Entrar".
- **Estados:** visitante (padrão); indisponibilidade geral (faixa com texto e horário previsto quando houver,
  sem formulários ativos); manutenção (página estática, sem "Criar conta").

## Cadastro

- **Propósito:** criar uma conta com o mínimo de dados; a idade é verificada depois, pelo fornecedor
  (spec 12.2).
- **Conteúdo:** e-mail, senha (com regras visíveis antes de digitar), aceite das versões dos termos com links.
- **Ações:** primária "Criar conta"; neutra "Já tenho conta ›".
- **Regras:** `autocomplete="email"` e `"new-password"`; colar e gerenciador de senhas permitidos; mostrar/ocultar
  senha; nenhum desafio cognitivo como único caminho (A11Y-01; DI cap. 10, "Input Hints", "Password Strength
  Meter").

```text
┌──────────────────────────────────────┐
│ Duora                                │
├──────────────────────────────────────┤
│ Criar conta                          │
│                                      │
│ E-mail                               │
│ ┌──────────────────────────────────┐ │
│ │                                  │ │
│ └──────────────────────────────────┘ │
│ Senha                                │
│ [regras da senha: política a definir]│
│ ┌────────────────────────────┬─────┐ │
│ │                            │Mostr│ │
│ └────────────────────────────┴─────┘ │
│                                      │
│ ☐ Li e aceito os Termos de uso e a   │
│   Política de privacidade (links)    │
│                                      │
│ [■        Criar conta              ] │
│                                      │
│ Já tenho conta ›                     │
│                                      │
│ Depois do cadastro, confirmamos seu  │
│ e-mail e sua idade. Ninguém vê seu   │
│ e-mail.                              │
└──────────────────────────────────────┘
```

| Estado | Apresentação |
|---|---|
| Padrão | Campos vazios, botão habilitado (validação ao enviar, não ao digitar) |
| Enviando | Botão com indicador e `aria-busy`, campos somente leitura |
| Erro de campo | Resumo no topo com links para cada campo; mensagem em texto sob o campo; valores válidos preservados |
| Erro neutro | "Se o e-mail puder ser usado, você receberá uma mensagem" — mesma resposta para e-mail existente ou não (spec 8.2) |
| Limite temporário | "Muitas tentativas. Tente de novo às 21:15." com horário absoluto; botão desabilitado até lá |
| E-mail pendente | Tela "Confirme seu e-mail": endereço mascarado, "Reenviar" (secundária, com limite), "Corrigir e-mail ›" |
| `loading` / `offline` | Formulário não depende de carga; offline desabilita o envio com faixa e mantém o que foi digitado |

## Login

- **Propósito:** entrar com credenciais e, quando exigido, segundo fator.
- **Conteúdo:** e-mail, senha, "Esqueci minha senha", passo de MFA quando aplicável.
- **Ações:** primária "Entrar"; neutras "Esqueci minha senha ›" e "Criar conta ›".

```text
┌──────────────────────────────────────┐
│ Duora                                │
├──────────────────────────────────────┤
│ ┌──────────────────────────────────┐ │
│ │ ⓘ Sua sessão terminou. Entre de  │ │
│ │   novo para continuar.           │ │
│ └──────────────────────────────────┘ │
│ Entrar                               │
│                                      │
│ E-mail                               │
│ ┌──────────────────────────────────┐ │
│ │ ana@example.com                  │ │
│ └──────────────────────────────────┘ │
│ Senha                                │
│ ┌────────────────────────────┬─────┐ │
│ │ ••••••••••                 │Mostr│ │
│ └────────────────────────────┴─────┘ │
│ Esqueci minha senha ›                │
│                                      │
│ [■           Entrar                ] │
│                                      │
│ Ainda não tem conta? Criar conta ›   │
└──────────────────────────────────────┘
```

| Estado | Apresentação |
|---|---|
| Padrão | Sem aviso no topo |
| Sessão expirada | Aviso informativo no topo (desenho acima); após entrar, volta à rota anterior se ainda autorizada |
| Enviando | Botão pendente |
| Erro neutro | "E-mail ou senha incorretos." — nunca indica qual dos dois nem se a conta existe |
| Limite temporário | Horário absoluto de nova tentativa; link de recuperação continua disponível |
| MFA | Tela própria: campo de código com `autocomplete="one-time-code"`, "Usar outro método ›" |
| Recuperação | "Esqueci minha senha" → e-mail → resposta neutra igual para conta existente ou não |

## Elegibilidade

- **Propósito:** mostrar a situação da selfie de presença e o próximo passo, sem expor o motivo técnico do
  fornecedor.
- **Conteúdo:** etapa atual da entrada (e-mail ✓, elegibilidade, termos, perfil), estado, motivo comunicável.
- **Ações:** primária conforme o estado ("Iniciar verificação", "Tentar novamente"); neutra "Excluir conta ›"
  sempre disponível (spec 4.1, desistência).
- **Estados:** pendente (instrução do que será pedido e por quem); processamento (texto "Isso pode levar alguns
  minutos; você pode fechar esta página"); revisão (prazo esperado, sem liberar acesso); falha de fornecedor
  ("O serviço de verificação está indisponível. Sua conta continua pendente."); acesso não autorizado (texto
  neutro + contato do atendimento). `loading`, `error` e `offline` conforme as convenções.

## Perfil próprio

- **Propósito:** editar o que outras pessoas veem e as preferências que só o sistema usa.
- **Conteúdo:** nome de exibição, faixa etária pública, região aproximada, descrição curta, até 6 fotos
  aprovadas, preferências privadas. Cada bloco com rótulo "Visível para pares" ou "Só você e o sistema".
- **Ações:** primária "Salvar alterações"; secundária "Adicionar foto"; seção Conta com e-mail, senha e MFA.
- **Estados:** rascunho (alterações não salvas indicadas em texto; nada íntimo salvo localmente); upload
  (progresso em texto e porcentagem); quarentena/processamento ("Em análise; ainda não aparece para ninguém");
  rejeição (motivo comunicável e "Enviar outra"); conflito de edição ("Este perfil mudou em outro dispositivo"
  com "Ver versão atual" e "Reaplicar minhas alterações", spec 13.5); salvo (confirmação em `role="status"`).

## Eventos

### Lista de eventos

- **Propósito:** encontrar eventos autorizados e ver a própria participação.
- **Conteúdo:** cards com título, data e horário com fuso, mostrando os dois fusos quando o do evento e o da pessoa
  diferem (ADR-0026). Exemplo de um evento às 20:00 em São Paulo (`America/Sao_Paulo`) visto de Boston
  (`America/New_York`), com `Intl.DateTimeFormat` e `timeZoneName: 'short'` (saída conferida no Node 24): em PT-BR,
  "sex., 16 de out., 20:00 BRT (19:00 GMT-4 no seu fuso)"; em EN-US, "Fri, Oct 16, 8:00 PM GMT-3 (7:00 PM EDT
  your time)". A abreviação só sai por nome no fuso "da casa" do idioma; nos outros, o `Intl` mostra o deslocamento
  (`GMT-4`, `GMT-3`). Quando os fusos coincidem, só um aparece ("sex., 16 de out., 20:00 BRT"). Formato mais legível
  (`shortGeneric`) em proposta na issue #81. Regras resumidas, situação própria. Sem lista de participantes nem contadores de popularidade.
- **Ações:** cada card é um link para o detalhe.
- **Estados:** `loading` (skeleton de 3 cards); `empty` ("Nenhum evento aberto agora. Avisaremos por e-mail
  quando houver." + "Ajustar avisos ›"); `error`; `offline`.

### Detalhe do evento

- **Propósito:** decidir participar e saber exatamente quando e como.
- **Conteúdo:** horário local com fuso, duração, número de rodadas, como funciona a experiência, regras de
  convivência, situação da vaga, abertura do check-in.
- **Ações:** uma primária, que muda com o estado ("Inscrever-me", "Entrar na lista de espera", "Fazer check-in",
  "Ir para a sala de espera"); secundária "Cancelar inscrição" quando inscrito.

```text
┌──────────────────────────────────────┐
│ ‹ Eventos                            │
├──────────────────────────────────────┤
│ Noite de sexta: Rotas Improváveis    │
│                                      │
│ sex., 16 de out. · 20:00 – 21:00 BRT │
│ 3 rodadas de até 12 minutos          │
│                                      │
│ ┌──────────────────────────────────┐ │
│ │ ✓ Você está inscrita.            │ │
│ │ O check-in abre às 19:50.        │ │
│ └──────────────────────────────────┘ │
│                                      │
│ Como funciona                        │
│ 1. Faça check-in até o início.       │
│ 2. A cada rodada, você recebe um     │
│    convite para jogar com alguém.    │
│ 3. Depois, decide em privado se      │
│    quer continuar a conversa.        │
│                                      │
│ Regras de convivência ›              │
│ O que a outra pessoa vê ›            │
│                                      │
│ [■   Fazer check-in (abre 19:50)   ] │
│ [      Cancelar inscrição          ] │
├──────────────────────────────────────┤
│ Eventos  Conversas  Segurança  Perfil│
└──────────────────────────────────────┘
```

| Estado | Apresentação e ação primária |
|---|---|
| Sem eventos | Ver lista de eventos (`empty`) |
| Disponível | "Inscrever-me" |
| Lista de espera | "Você está na posição 4 da lista de espera"; primária nenhuma; secundária "Sair da lista" |
| Inscrito | Desenho acima; check-in desabilitado com horário até abrir |
| Check-in aberto | "Fazer check-in" habilitado |
| Lotado | "Evento lotado. A lista de espera está aberta." — fato, sem contagem regressiva; "Entrar na lista de espera" |
| Cancelado | Motivo comunicável; "Ver outros eventos" |
| Pausado | "O evento está pausado. Você continua inscrita."; sem ação primária |
| Alteração de horário | Faixa destacando o novo horário e o antigo riscado; "Manter inscrição" / "Cancelar inscrição" |
| `loading` / `error` / `offline` | Conforme as convenções |

## Sala de espera

- **Propósito:** aguardar a rodada com presença confirmada e receber o convite.
- **Conteúdo:** rodada atual de N, horário da próxima, presença própria, convite quando chegar, aviso de que o
  convite dura 60 segundos (dito antes de chegar, A11Y-08).
- **Ações:** primária só quando há convite ("Aceitar convite"); "Sair do evento" fixo no cabeçalho do modo
  focado.

```text
┌──────────────────────────────────────┐
│ Sala de espera            [ Sair ]   │
├──────────────────────────────────────┤
│ Rodada 2 de 3 · começa às 20:14      │
│                                      │
│ ● Você está presente.                │
│                                      │
│ Quando a rodada começar, você recebe │
│ um convite. Ele vale por 60 segundos │
│ e avisaremos com som e texto, se você│
│ permitir.                            │
│                                      │
│ ┌──────────────────────────────────┐ │
│ │ Convite para jogar               │ │
│ │ Rotas Improváveis · até 8 min    │ │
│ │ Responda até 20:15:00            │ │
│ │ (cerca de 50 s)                  │ │
│ │                                  │ │
│ │ [■     Aceitar convite         ] │ │
│ │ [         Recusar              ] │ │
│ └──────────────────────────────────┘ │
│                                      │
│ Recusar não tem penalidade. Você     │
│ continua no evento.                  │
└──────────────────────────────────────┘
```

| Estado | Apresentação |
|---|---|
| Aguardando | Sem o card de convite; texto do horário da rodada |
| Convite recebido | Card acima; chegada anunciada uma vez em `role="status"`; foco **não** é movido |
| Aceito, aguardando a outra pessoa | "Convite aceito. Aguardando a outra pessoa." |
| Convite expirado | "O convite expirou. Você continua na rodada." Sem culpa, sem indicar quem não aceitou |
| Outro participante ausente | "Não foi possível formar um par nesta rodada. Você continua no evento." (spec 5) |
| Evento pausado | "O evento está pausado." + horário previsto quando houver |
| Reconectando / `offline` | Faixa de status; o card de convite reaparece com o estado do servidor após reconectar |
| `loading` / `error` | Conforme as convenções; `error` mantém "Sair" |

## Experiência (Rotas Improváveis)

- **Propósito:** construir com a outra pessoa um roteiro fictício a partir de pistas complementares (spec 6.2).
- **Conteúdo:** etapa nomeada ("Etapa 2 de 4: pistas"), objetivo, as **próprias** pistas com o rótulo "Só você
  vê", opções aprovadas como botões, roteiro compartilhado, chat da sala, faixa de prazo.
- **Ações:** uma primária por etapa ("Entendi" na introdução, "Escolher" na escolha, "Confirmar roteiro" no
  ajuste, "Ir para a decisão" na conclusão); secundária "Pedir revisão" no ajuste; "Sair" fixo; "Denunciar"
  dentro do diálogo de saída e no menu do chat.
- **Regras:** nenhuma ação depende de arrastar, cor, áudio, voz do par ou reflexo; todas as escolhas por botão
  e teclado (A11Y-04); o resultado não é nota de compatibilidade; a pista da outra pessoa não está no payload
  antes da revelação (spec 6.2).
- **Mídia (proposto, spec 6.5):** faixa de cenário no topo (até ~30% da altura no mobile), com no máximo um
  personagem; Controle de som na barra, mudo por padrão; transcrição das falas abaixo do cenário; na etapa de
  escolha, só ambiente baixo ou silêncio; nenhum som reage ao prazo.
- **Voz ao vivo (proposto, ADR-0028):** botão "Entrar na voz" no painel do chat. Ninguém ouve ninguém até as
  duas pessoas tocarem nele; até lá a tela diz "Aguardando a outra pessoa entrar na voz" (sem prazo nem
  insistência). Dentro da voz, "Ligar meu microfone" é um gesto separado, e a permissão do navegador só é pedida
  nesse toque. Ligada, mostra "Microfone aberto", quem está falando, "Silenciar Bia" (Bia não é avisada),
  "Desligar meu microfone", "Sair da voz", "Bloquear" e "Denunciar" (com a categoria "suspeita de menor"). O chat de texto
  continua ao lado: a experiência termina igual sem voz. Aviso na tela de voz: "A outra pessoa pode gravar o
  que você diz no próprio aparelho."

```text
┌──────────────────────────────────────┐
│ Rotas Improváveis   [Som] [ Sair ]   │
│ Etapa 3 de 4: escolhas               │
│ Termina às 20:22 · cerca de 6 min    │
├──────────────────────────────────────┤
│ ░░░░░ cenário: cais ao entardecer ░░ │
│ Guia: "Toda rota começa por um porto"│
│ (voz editorial)                      │
├──────────────────────────────────────┤
│ Objetivo: montar juntos um passeio   │
│ por uma cidade inventada.            │
│                                      │
│ Suas pistas · só você vê             │
│ • O passeio começa perto da água.    │
│ • Nada depois das 18h no roteiro.    │
│                                      │
│ Sua vez: escolha a primeira parada   │
│ ( ) Farol desativado                 │
│ ( ) Mercado flutuante                │
│ ( ) Biblioteca subterrânea           │
│                                      │
│ [■           Escolher              ] │
│                                      │
│ Roteiro até agora                    │
│ 1. (vazio)                           │
├──────────────────────────────────────┤
│ Chat da sala      [ Ligar microfone ]│
│ Bia: começo pelo farol?              │
│ ┌────────────────────────────┐┌────┐ │
│ │ Mensagem                   ││Env.│ │
│ └────────────────────────────┘└────┘ │
└──────────────────────────────────────┘
```

O diálogo "Sair" (A11Y-03), aberto pelo botão do cabeçalho:

```text
┌──────────────────────────────────────┐
│ Sair da sala?                    [×] │
│                                      │
│ Você volta para a sala de espera. A  │
│ outra pessoa verá só que a sala foi  │
│ encerrada.                           │
│                                      │
│ [■         Sair agora              ] │
│ [  Sair e bloquear esta pessoa     ] │
│ [  Sair e denunciar                ] │
│ Cancelar ›                           │
└──────────────────────────────────────┘
```

"Sair agora" tem o foco inicial e não exige formulário. Denunciar abre o formulário só depois de sair; o
bloqueio pede confirmação própria com a primária destrutiva "Bloquear pessoa".

| Estado | Apresentação |
|---|---|
| Ativa | Desenho acima |
| Ação pendente | Botão "Escolher" com indicador; opções somente leitura; repetição não consome duas escolhas (spec 6.2) |
| Aguardando a outra pessoa | "Aguardando a escolha da outra pessoa" em `role="status"` |
| Conflito | "O jogo avançou enquanto você escolhia. Atualizamos a tela." Estado do servidor recarregado; foco no título da etapa |
| Reconectando | Faixa "Reconectando… sua vaga fica reservada até 20:19"; controles desabilitados; "Sair" ativo |
| Prazo: aviso | Aos 2 min e aos 30 s restantes, faixa de prazo muda de texto e é anunciada uma vez (A11Y-08) |
| Prazo encerrado | "O tempo da rodada terminou." + resultado do que foi concluído + "Ir para a decisão" |
| Sala abortada | "Esta sala foi encerrada." Mensagem neutra, sem motivo que exponha a outra pessoa; "Voltar à sala de espera"; "Denunciar ›" |
| Conclusão | Roteiro compartilhado, sem nota; "Ir para a decisão"; sem fanfarra nem som de recompensa |
| Som ligado | Música e ambiente do cenário; falas com transcrição; ducking sob a voz; "Reduzir áudio" deixa só voz e interface |
| Voz ao vivo: permissão negada | "O microfone não foi liberado. O chat continua disponível." + como liberar; nada bloqueia a experiência |
| Voz ao vivo: ativa | Indicadores de microfone aberto e de quem fala, em texto; silenciar, desligar e sair da voz sem sair da sala |
| `loading` / `error` / `offline` | Conforme as convenções; "Sair" disponível em todos |

## Decisão privada

- **Propósito:** registrar, em privado, se quer continuar a conversa com a pessoa.
- **Conteúdo:** explicação de privacidade, a escolha, o prazo de alteração em horário absoluto, o que acontece
  se for mútuo. Nunca: decisão, horário ou status da outra pessoa (spec 7.1).
- **Ações:** primária "Salvar decisão"; as duas escolhas com o mesmo peso visual.
- **Som:** silêncio ou ambiente neutro; nenhum som ao salvar, ao formar conexão nem ao expirar; a voz ao vivo da
  sala já terminou (spec 6.5).

```text
┌──────────────────────────────────────┐
│ Sua decisão                [ Sair ]  │
├──────────────────────────────────────┤
│ Quer continuar conversando com Bia?  │
│                                      │
│ Só você vê esta escolha. Bia nunca   │
│ saberá se você disse não.            │
│                                      │
│ ┌──────────────────────────────────┐ │
│ │ ( ) Tenho interesse              │ │
│ └──────────────────────────────────┘ │
│ ┌──────────────────────────────────┐ │
│ │ ( ) Não tenho interesse          │ │
│ └──────────────────────────────────┘ │
│                                      │
│ Se as duas pessoas tiverem interesse,│
│ uma conversa abre em Conversas.      │
│ Você pode mudar até sex., 17 out.,   │
│ 20:22.                               │
│                                      │
│ [■        Salvar decisão           ] │
│                                      │
│ Voltar à sala de espera ›            │
└──────────────────────────────────────┘
```

| Estado | Apresentação |
|---|---|
| Sem escolha | Nenhum rádio marcado; "Salvar decisão" mostra erro em texto se enviado vazio |
| Salvando | Botão pendente; rádios somente leitura |
| Escolha salva | "Decisão salva. Só você vê." em `role="status"`; escolha marcada |
| Alteração permitida | Primária vira "Alterar decisão" (secundária até haver mudança) |
| Prazo encerrado | "A janela de decisão terminou." — nunca "não houve interesse" (spec 4.3) |
| Conexão formada | "Vocês dois têm interesse. A conversa está em Conversas." + "Abrir conversa"; alteração dá lugar a "Encerrar conexão" em Conversas |
| `loading` / `error` / `offline` | Conforme as convenções; `error` preserva a escolha não salva na tela |

## Conversas

### Lista

- **Propósito:** ver conexões ativas e retomar conversas.
- **Conteúdo:** nome, avatar aprovado, trecho da última mensagem própria ou recebida, indicador de não lidas
  com número em texto. Sem "visto por último" detalhado (spec 7.2).
- **Estados:** `loading`; `empty` ("Quando houver interesse mútuo depois de um encontro, a conversa aparece aqui."
  + "Ver eventos"); `error` ("Não foi possível carregar suas conversas" — nunca "você não tem conversas",
  spec 13.2); `offline`.

### Conversa

- **Propósito:** conversar depois de uma conexão.
- **Conteúdo:** histórico por cursor, composer com contador de 2.000 caracteres, menu "Opções da conversa".
- **Ações:** primária "Enviar"; menu com "Encerrar conexão", "Bloquear" e "Denunciar", com rótulos.

```text
┌──────────────────────────────────────┐
│ ‹ Conversas   Bia     [ Opções ▾ ]   │
├──────────────────────────────────────┤
│        Carregar mensagens anteriores │
│                                      │
│ Bia · 20:31                          │
│ ┌────────────────────────┐           │
│ │ Gostei do farol :)     │           │
│ └────────────────────────┘           │
│                  Você · 20:33        │
│           ┌────────────────────────┐ │
│           │ Eu também! E o mercado?│ │
│           └────────────────────────┘ │
│                        Enviada ✓     │
│           ┌────────────────────────┐ │
│           │ Amanhã você pode?      │ │
│           └────────────────────────┘ │
│              ⚠ Não enviada.          │
│              Tentar de novo ›        │
├──────────────────────────────────────┤
│ Mensagem              1.980 restantes│
│ ┌────────────────────────────┐┌────┐ │
│ │                            ││Env.│ │
│ └────────────────────────────┘└────┘ │
└──────────────────────────────────────┘
```

| Estado | Apresentação |
|---|---|
| Vazio | "Vocês se conectaram em 16 out. Comece a conversa." |
| Carregando | Skeleton no histórico; composer já utilizável |
| `sending` / `sent` / `failed` | "Enviando…", "Enviada ✓", "⚠ Não enviada." + "Tentar de novo" (texto e ícone; nunca "lida", spec 7.2) |
| Reconciliação | Após reconectar, histórico reconsultado; mensagens novas entram sem mover o foco nem a posição de leitura |
| Encerramento | "Esta conexão foi encerrada." Composer removido; histórico conforme a política |
| Indisponibilidade | "Esta conversa não está disponível." — igual para bloqueio, suspensão ou encerramento pela outra pessoa (spec 7.3) |
| `error` / `offline` | Composer preserva o texto digitado; envio desabilitado com motivo |

## Segurança e privacidade

- **Propósito:** reunir tudo o que protege a pessoa: bloqueios, denúncias, sessões, exportação e exclusão.
- **Conteúdo:** seções com título e resumo; cada denúncia com protocolo e estado permitido; sessões ativas com
  dispositivo aproximado e "Encerrar".
- **Ações:** sem primária global (é uma tela de navegação, DI cap. 2, "Menu Page"); cada seção tem sua ação.

```text
┌──────────────────────────────────────┐
│ Duora                                │
├──────────────────────────────────────┤
│ Segurança e privacidade              │
│                                      │
│ Em perigo agora? Ligue 190.          │
│                                      │
│ Bloqueios                          › │
│ 2 pessoas bloqueadas                 │
│ ──────────────────────────────────── │
│ Minhas denúncias                   › │
│ 1 em revisão · protocolo D-4F2K      │
│ ──────────────────────────────────── │
│ Conexões                           › │
│ Encerrar ou bloquear uma conexão     │
│ ──────────────────────────────────── │
│ Sessões ativas                     › │
│ Este Mac e 1 outro dispositivo       │
│ ──────────────────────────────────── │
│ Exportar meus dados                › │
│ ──────────────────────────────────── │
│ Excluir minha conta                › │
│                                      │
├──────────────────────────────────────┤
│ Eventos  Conversas [Segurança] Perfil│
└──────────────────────────────────────┘
```

| Estado | Apresentação |
|---|---|
| Pedido recebido | Protocolo visível e copiável; "Você pode acompanhar aqui" |
| Revisão | Estado em texto ("Em revisão desde 16 out.") sem expor a outra parte |
| Concluído | Resultado comunicável; "Recorrer" quando permitido (spec 4.2, `Appealed`) |
| Recuperação | Exportação com link expirado: "O link expirou. Solicitar nova exportação." |
| Erro operacional | "Não foi possível concluir agora. Seu pedido não foi perdido." quando o servidor confirmou o recebimento; caso contrário, "Tentar de novo" |
| Exclusão | Reautenticação, consequências em lista, confirmação com primária destrutiva "Excluir conta" e "Cancelar" neutro |
| `loading` / `empty` / `error` / `offline` | `empty` por seção ("Nenhum bloqueio"); demais conforme as convenções |

O texto de emergência e o número local são conteúdo a validar pelo responsável pelo produto antes do piloto.

## Administração

- **Propósito:** operar eventos, conteúdo, casos atribuídos e saúde funcional, com privilégio mínimo
  (spec 1.3). Carregada como bundle separado (spec 13.5).
- **Ações:** uma primária por tarefa; ações críticas com step-up de autenticação (spec 4.3).
- **Estados:** permissão insuficiente (403 com texto, sem listar o que existe); conflito (versão mudou, recarregar
  e reaplicar); evidência restrita (acesso registrado, conteúdo mostrado só no recorte autorizado);
  confirmação (resumo do efeito antes de aplicar); trilha de auditoria (quem, quando, o quê, visível no caso).
  `loading`, `empty`, `error` e `offline` conforme as convenções.
- **Wireframes:** fora deste documento; a administração entra no fluxo de design quando o marco correspondente
  começar.
