# Acessibilidade: critérios e matriz de suporte

O que o Duora precisa atender, em quais combinações de navegador e tecnologia assistiva, e o que a automação
cobre (spec [13.4](../product/spec.md#134-acessibilidade-como-requisito-verificável) e
[13.7](../product/spec.md#a11y-protocolo)). **Como** cada critério é verificado, passo a passo, e como a
evidência é registrada está no [protocolo de teste](accessibility-test-protocol.md); este documento não repete
procedimentos.

| Este documento | O protocolo |
|---|---|
| Meta e escopo de conformidade | Preparação do ambiente (macOS 27, Safari, VoiceOver) |
| Critérios de projeto por verificação A11Y-01 a A11Y-08 e critérios WCAG relacionados | Passos executáveis de A11Y-01 a A11Y-08 |
| Matriz de suporte de navegador e leitor de tela | Modelo de registro de evidência |
| Cobertura e limites da automação (axe) | Regras de status e de aceite T-23 |
| Justificativa por fluxo para WCAG 2.2.1 (tempo) | Verificação de que a justificativa foi implementada |

## Meta e escopo

- **Meta:** WCAG 2.2, níveis A e AA, avaliada nas **jornadas completas** da spec 4.1, não em componentes
  isolados (D-DES-02, D-DES-07). Um componente com primitiva acessível não certifica a página.
- **Escopo da avaliação** (WCAG-EM, D-DES-07): todas as telas de [screens.md](screens.md) em todos os estados
  obrigatórios, incluindo erro, sessão expirada, reconexão, telas do fornecedor de verificação etária
  incorporadas ao fluxo e as telas administrativas usadas no piloto.
- **Escolhas do Duora acima do AA**, registradas como meta de projeto e não como requisito normativo:
  alvo de 44 × 44 CSS px (o AA de 2.5.8 é 24 px com exceções) e variante sem animação para
  `prefers-reduced-motion` (2.3.3 é AAA).
- **Status de cada verificação:** falha, atendido, não aplicável com justificativa ou não avaliado. A
  definição e as regras estão no [protocolo](accessibility-test-protocol.md#status). Scanner sem violações não
  transforma "não avaliado" em "atendido" (spec 13.7).

## Critérios por verificação

Os critérios de projeto vêm da tabela da spec 13.7; a coluna WCAG lista os critérios de sucesso 2.2 que cada
verificação exercita. Os componentes citados estão no [inventário](system.md#inventário-de-componentes).

### A11Y-01 · Cadastro, login e recuperação

- Label visível e persistente em todo campo; ajuda antes do campo, não só no placeholder.
- `autocomplete` com o propósito correto (`email`, `current-password`, `new-password`, `one-time-code`).
- Colar, preencher automaticamente e usar gerenciador de senhas sempre permitidos; nenhum teste cognitivo
  como único caminho.
- Erro identificado em texto, associado ao campo e resumido no topo; valores válidos preservados.
- Respostas neutras de cadastro e recuperação são compreensíveis sem revelar se a conta existe.
- **WCAG:** 1.3.1, 1.3.5, 2.4.6, 3.3.1, 3.3.2, 3.3.3, 3.3.7, 3.3.8, 4.1.2.

### A11Y-02 · Navegação e mudanças de rota

- `<title>` único por rota; um `h1` por tela; landmarks `banner`, `navigation`, `main`, `contentinfo`.
- Link "Pular para o conteúdo" como primeiro item focável (já em `AppLayout.tsx`).
- Em navegação de SPA, o foco vai para o `h1` ou o `main` da nova rota e a mudança é anunciada.
- Navegação global igual em todas as telas fora do modo focado; Segurança no mesmo lugar sempre (ajuda
  consistente).
- Nenhuma armadilha de teclado; ordem de foco segue a ordem visual.
- **WCAG:** 1.3.1, 2.1.1, 2.1.2, 2.4.1, 2.4.2, 2.4.3, 2.4.6, 3.2.3, 3.2.4, 3.2.6.

### A11Y-03 · Diálogos de sair, bloquear e denunciar

- Padrão APG Dialog Modal (D-DES-03): nome acessível pelo título, foco inicial na ação segura e principal
  ("Sair agora"), Tab contido no diálogo, Esc fecha, foco volta ao gatilho.
- Fechar ("Cancelar", ×) e agir ("Sair agora", "Bloquear pessoa") são controles separados e rotulados.
- Sair não depende de preencher formulário; denunciar e bloquear são opcionais e separados (spec 7.3).
- **WCAG:** 1.3.1, 2.1.1, 2.1.2, 2.4.3, 2.4.7, 2.4.11, 3.2.2, 4.1.2.

### A11Y-04 · Minijogos

- Toda ação do jogo por botão, rádio ou campo nativo; nada exige arrastar, gesto de vários dedos, cor, som ou
  reflexo.
- Pistas e estado do roteiro em texto; "só você vê" dito em texto, não por ícone.
- A interação alternativa é a interação padrão: não existe "modo acessível" separado com regras ou
  privacidade diferentes.
- `DescribeAccessibleInteraction` (spec 6.1) fornece a instrução textual de cada tipo de jogo.
- **WCAG:** 1.1.1, 1.3.1, 1.3.3, 1.4.1, 2.1.1, 2.5.1, 2.5.7, 4.1.2.

### A11Y-05 · Chat e reconexão

- Histórico com `role="log"`; chegada de mensagem não move o foco nem a posição de leitura.
- Estados de envio (`Enviando`, `Enviada`, `Não enviada`) em texto, alcançáveis pelo leitor de tela.
- Reconexão e "Sem conexão" em `role="status"`, anunciados uma vez por mudança, não por tentativa.
- O relógio não é região viva; só os marcos de prazo de A11Y-08 são anunciados.
- **WCAG:** 1.3.1, 1.3.2, 2.2.2, 3.2.1, 3.2.2, 4.1.3.

### A11Y-06 · Layout e zoom

- Conteúdo e controles completos com zoom de texto a 200% e em reflow equivalente a 320 CSS px, sem rolagem
  horizontal (exceto conteúdo que exige duas dimensões).
- Contraste de texto ≥ 4,5:1 (grande ≥ 3:1) e de componentes e foco ≥ 3:1, medido em todos os estados reais,
  inclusive erro e desabilitado (teste `design-tokens.test.ts` para os pares de token; inspeção para
  combinações compostas).
- Foco visível e nunca totalmente encoberto por cabeçalho fixo, barra inferior, teclado virtual ou toast.
- Espaçamento de texto aumentado (1.4.12) não corta conteúdo.
- **WCAG:** 1.4.3, 1.4.4, 1.4.10, 1.4.11, 1.4.12, 1.4.13, 2.4.7, 2.4.11.

### A11Y-07 · Alvos e movimento

- Alvo mínimo normativo: 24 × 24 CSS px ou espaçamento equivalente (2.5.8). Meta do Duora: 44 × 44 px para
  todo controle de ação (`min-h-11` no botão).
- Com `prefers-reduced-motion: reduce`, nenhuma transição ou animação decorativa; estado final visível.
- Nada pisca mais de três vezes por segundo.
- **WCAG:** 2.2.2, 2.3.1, 2.5.8. Meta de projeto: 2.3.3 (AAA) e 2.5.5 (AAA).

### A11Y-08 · Prazos e segurança

- Todo prazo é dito **antes** de começar, em texto e em horário absoluto, e o tempo restante é mostrado em
  minutos.
- Avisos em marcos (2 min e 30 s), anunciados uma vez; nenhum anúncio por segundo.
- Nenhuma tarefa depende de reflexo ou velocidade de leitura; o prazo é da rodada, não de cada clique.
- Sair, bloquear e denunciar nunca têm prazo e continuam disponíveis após o fim do prazo.
- Cada limite de tempo tem justificativa própria para 2.2.1, na tabela abaixo; o app não é declarado isento
  por conter um jogo ao vivo (spec 13.7).
- **WCAG:** 2.2.1, 2.2.2, 4.1.3.

## WCAG 2.2.1 (Timing Adjustable): justificativa por fluxo

O critério 2.2.1 exige, para cada limite de tempo definido pelo conteúdo, uma das opções: desligar, ajustar
(até 10×), estender (aviso com pelo menos 20 s e ação simples, pelo menos 10 vezes), exceção de tempo real,
exceção essencial ou limite acima de 20 horas (D-DES-02). A tabela é **proposta de justificativa**; cada
linha recebe status no protocolo (A11Y-08) e só vale depois de verificada.

| Fluxo | Limite (spec 4.3) | Opção de 2.2.1 invocada | Justificativa | Mitigações obrigatórias |
|---|---|---|---|---|
| Convite da rodada | 60 s | Exceção de tempo real | O convite coordena duas pessoas numa rodada que acontece ao mesmo tempo para todo o evento; esperar uma pessoa prende a outra e atrasa as salas seguintes. Não existe alternativa sem prazo que preserve a rodada | Duração dita na sala de espera antes do convite; chegada anunciada; horário absoluto; recusar ou deixar expirar não tem penalidade e a pessoa continua no evento |
| Jogo ativo (Rotas Improváveis, Constelação de Escolhas) | Até 8 min, dentro da rodada de 12 min | Exceção de tempo real | A sessão é compartilhada e simultânea entre duas pessoas, e o fim da rodada é o mesmo para o evento inteiro; estender uma sala desalinharia o pareamento das rodadas seguintes | Nenhuma ação individual tem prazo; avisos aos 2 min e 30 s; ao terminar, o que foi concluído é preservado e a pessoa segue para a decisão; nenhuma pontuação depende de velocidade |
| Etapas do jogo (introdução, pistas, escolhas) | Sem limite próprio além do jogo | Não aplicável | A etapa não tem prazo separado | Se algum jogo futuro introduzir prazo por etapa, esta tabela é revisada antes da publicação da versão |
| Tolerância de reconexão | 90 s | Exceção de tempo real | Manter a sala reservada além disso prende a outra pessoa numa sala inativa durante uma rodada ao vivo | Faixa com horário-limite; a saída continua disponível; perder a sala não gera penalidade |
| Check-in | Abre 10 min antes; só entra quem confirmou presença até o início | Exceção de tempo real | O evento acontece em horário marcado para todo o grupo | Horário de abertura e de início ditos no detalhe do evento e no lembrete |
| Chat da sala | Durante o período da sala | Exceção de tempo real | O chat existe para a sala ao vivo; depois dela, só há conversa persistente com conexão (spec 7.2) | O fim do chat é anunciado com o fim da sala; o texto não enviado continua visível para copiar |
| Janela de decisão | 24 h | Limite acima de 20 horas | Atende diretamente o critério | Prazo exibido em data e hora absolutas; expirar não é apresentado como rejeição |
| Link de exportação | 24 h | Limite acima de 20 horas | Atende diretamente o critério | Link expirado orienta nova solicitação |
| Sessão por inatividade | 30 min | Estender | Não é tempo real nem essencial; precisa cumprir 2.2.1 por extensão | Aviso pelo menos 2 min antes, com "Continuar conectada" (ação simples) e foco no diálogo; extensões ilimitadas enquanto houver atividade; o aviso não aparece durante uma sala ativa, pois a atividade da sala renova a sessão |
| Sessão absoluta | 12 h | **Pendente de decisão** | Limite de segurança (spec 4.3) que não pode ser estendido sem mudar a política; 2.2.1 não prevê exceção específica para segurança | Proposta: aviso antecipado; após reautenticar, voltar à mesma tela com o estado do servidor preservado. A decisão entre aceitar o risco registrado ou ajustar a política é do responsável pelo produto |
| Limite temporário de tentativas (login, reenvio) | Variável | Não aplicável com justificativa | É bloqueio de segurança após tentativas, não limite para concluir uma tarefa em andamento | Horário absoluto de nova tentativa; caminho alternativo de recuperação |

## Matriz de suporte

As versões exatas são registradas a cada execução no [registro de evidência](accessibility-test-protocol.md#modelo-de-registro-de-evidência).
Testar uma combinação não autoriza prometer compatibilidade com as outras (spec 13.7).

| Combinação | Sistema | Exigência | Escopo | Motivo |
|---|---|---|---|---|
| Safari + VoiceOver | macOS 27 | **Obrigatória** em todo release que muda jornada | A11Y-01 a A11Y-08, todas as jornadas | Combinação de referência da spec 13.7; disponível no ambiente de desenvolvimento |
| Safari, só teclado | macOS 27 | **Obrigatória** | A11Y-02, 03, 04, 06, 07 | Teclado sem leitor revela problemas de foco que o leitor mascara |
| Chrome e Firefox, só teclado | macOS 27 | **Obrigatória** antes do piloto | A11Y-02, 03, 04 | Diferenças de foco e de `<dialog>` entre motores |
| Safari + VoiceOver | iOS (versão da matriz de navegadores) | **Obrigatória** antes do piloto | A11Y-01, 03, 04, 05, 06, 07 | Safari/iOS está na matriz da spec 13.5; teclado virtual e gestos do leitor |
| Chrome + TalkBack | Android (versão da matriz) | **Obrigatória** antes do piloto | A11Y-01, 03, 04, 05, 06, 07 | Chrome/Android está na matriz da spec 13.5 |
| Firefox ou Chrome + NVDA | Windows | **A decidir** pelo público do piloto | Jornadas P1 do [brief](../product/experience-brief.md#tarefas-prioritárias) | Leitor mais usado no desktop Windows; exige máquina Windows |
| Playwright (Chromium e WebKit) + axe | CI (Ubuntu) | **Obrigatória** em todo PR | Páginas e estados com teste E2E | Já no pipeline; cobre só o que é detectável automaticamente |

## Automação: cobertura e limites

- **Ferramenta:** `@axe-core/playwright` (D-TEST-05), com as tags `wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa` e
  `wcag22aa`, fixadas em `frontend/e2e/smoke.spec.ts`. Ao atualizar o axe, conferir as tags e regras da versão
  instalada antes de afirmar cobertura de critérios 2.2.
- **Onde roda:** em cada página e **estado** relevante: erro de formulário, modal aberto, nova mensagem,
  reconexão, sessão expirada, estados vazios (spec 13.7). No Storybook, a verificação de acessibilidade roda
  em cada story (D-DES-04).
- **Assertions além do scanner:** nome acessível por `getByRole`, foco após navegação e após fechar diálogo,
  ordem de Tab nos diálogos, ausência de foco roubado ao receber mensagem.
- **O que a automação não cobre** e fica no protocolo manual: qualidade dos anúncios do leitor, compreensão de
  instruções e erros, foco encoberto, reflow real, uso de jogo por teclado de ponta a ponta, adequação das
  justificativas de 2.2.1 e experiência com movimento reduzido.

## Pendências

- Decisão sobre a sessão absoluta de 12 h e 2.2.1 (tabela acima).
- Decisão sobre incluir NVDA/Windows na matriz do piloto.
- Testes com pessoas com deficiência: complementam a inspeção técnica quando organizados (spec 13.7).
- Versões de iOS e Android da matriz de navegadores ainda não publicadas (spec 13.5).
