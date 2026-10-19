# Protocolo de teste de acessibilidade

Procedimento executável das verificações A11Y-01 a A11Y-08 (spec [13.7](../product/spec.md#a11y-protocolo)),
com o modelo de registro de evidência e as regras de status. **O que** cada verificação exige (critérios WCAG,
matriz de suporte, justificativas de tempo) está em [accessibility.md](accessibility.md); aqui ficam só os
passos. O aceite T-23 depende deste protocolo (spec 20.2).

Fontes dos comandos: VoiceOver User Guide para macOS 27 (D-DES-08); APG (D-DES-03); WCAG-EM e modelo de
relatório WAI (D-DES-07).

## Quando executar

| Gatilho | Escopo mínimo |
|---|---|
| PR que cria ou muda interação de uma jornada | Verificações da jornada afetada, em Safari + VoiceOver e só teclado |
| Mudança da matriz de suporte | Verificações afetadas na combinação nova |
| Antes do piloto (gate T-23) | A11Y-01 a A11Y-08 em todas as combinações obrigatórias de [accessibility.md](accessibility.md#matriz-de-suporte) |
| Nova versão de jogo publicada | A11Y-04 e A11Y-08 com a versão nova |

## Preparação (macOS 27)

Faça uma vez por máquina e confirme a cada execução; registre as versões no relatório.

1. **Navegador.** Safari na versão do sistema. Em Safari › Ajustes › Avançado, ligue "Pressionar Tab para
   realçar cada item em uma página web" (sem isso, Tab pula links). Ligue também o menu Desenvolvedor
   (necessário para o modo de design responsivo).
2. **Teclado do sistema.** Ajustes do Sistema › Teclado: ligue "Navegação por teclado".
3. **VoiceOver.** Liga e desliga com Command-F5. Modificador VO = Control-Option (ou Caps Lock). Comandos
   usados neste protocolo:

   | Ação | Comando |
   |---|---|
   | Abrir o rotor (títulos, links, campos, landmarks) | VO-U, setas para trocar de lista |
   | Ler a partir da posição atual | VO-A |
   | Mover o cursor do VoiceOver | VO-seta |
   | Ativar o item sob o cursor | VO-Espaço |
   | Entrar e sair de um grupo | VO-Shift-seta para baixo / para cima |
   | Ajuda de teclado e lista de comandos | VO-K / VO-H |

4. **Movimento reduzido.** Ajustes do Sistema › Acessibilidade › Tela › "Reduzir movimento".
5. **Dados de teste.** Ambiente local ou de homologação com contas sintéticas A e B (e C para terceiro não
   autorizado), evento de teste com rodada curta e a experiência publicada. Nunca use dados reais (spec 21.3).
6. **Duas pessoas.** A11Y-04 e A11Y-08 exigem duas sessões simultâneas: dois navegadores ou duas máquinas,
   sendo ao menos uma com a tecnologia assistiva em teste.

## Procedimentos

Cada passo tem o **resultado esperado**; qualquer divergência é registrada como falha com o passo, o que
aconteceu e a evidência. Execute primeiro só com teclado e depois com VoiceOver, salvo quando indicado.

### A11Y-01 · Cadastro, login e recuperação

1. Abra Criar conta. Com Tab, percorra os campos. **Esperado:** cada campo anuncia o rótulo visível e a ajuda;
   a ordem é e-mail, senha, mostrar senha, aceite dos termos, botão.
2. Envie o formulário vazio. **Esperado:** foco vai para o resumo de erros; cada erro é texto, anunciado e
   ligado ao campo; Tab a partir de um link do resumo leva ao campo.
3. Preencha um e-mail inválido e uma senha válida; envie. **Esperado:** a senha continua preenchida; só o
   e-mail tem erro.
4. Cole a senha a partir da área de transferência e use o preenchimento automático do gerenciador de senhas
   do Safari. **Esperado:** os dois funcionam; o Safari oferece senha forte em "nova senha".
5. Repita 1 a 4 em Entrar. Erre a senha. **Esperado:** mensagem neutra anunciada, sem indicar qual campo
   errou nem se a conta existe.
6. Em "Esqueci minha senha", envie um e-mail existente e um inexistente. **Esperado:** mesma mensagem e mesmo
   comportamento de foco.
7. Se MFA estiver habilitado, conclua o passo de código. **Esperado:** campo com rótulo; preenchimento
   automático de código permitido; nenhum quebra-cabeça como único caminho.
8. Com VoiceOver, abra o rotor (VO-U) na lista de campos. **Esperado:** todos os campos com nome, sem
   "campo de texto" anônimo.

### A11Y-02 · Navegação e mudanças de rota

1. Recarregue a página inicial; pressione Tab uma vez. **Esperado:** "Pular para o conteúdo" aparece e leva
   ao `main`.
2. Com VO-U, abra a lista de landmarks e a de títulos. **Esperado:** banner, navegação "Principal", main e
   rodapé; um `h1` coerente com o título da aba.
3. Navegue por Eventos → detalhe → Conversas → Segurança → Perfil só com teclado. **Esperado:** cada rota muda
   o título da aba e anuncia a nova tela; o foco vai para o `h1` ou o `main`; a ordem de foco segue a ordem
   visual; nenhum ponto prende o foco.
4. Volte com o histórico do navegador (Command-[). **Esperado:** a tela anterior é anunciada e o foco não se
   perde no topo do documento.
5. Confira que Segurança está no mesmo lugar e com o mesmo nome em todas as telas fora do modo focado.

### A11Y-03 · Diálogos de sair, bloquear e denunciar

1. Numa sala ativa, chegue a "Sair" só com teclado. **Esperado:** alcançável em poucas tabulações a partir do
   início da sala, com rótulo "Sair".
2. Ative "Sair". **Esperado:** o diálogo é anunciado com o título; o foco inicial está em "Sair agora".
3. Pressione Tab repetidamente. **Esperado:** o foco circula só dentro do diálogo.
4. Pressione Esc. **Esperado:** o diálogo fecha sem sair da sala e o foco volta ao botão "Sair".
5. Reabra e ative "Sair agora". **Esperado:** saída imediata, sem formulário; confirmação textual anunciada na
   tela de destino.
6. Repita com "Sair e bloquear esta pessoa". **Esperado:** confirmação própria com "Bloquear pessoa" como
   primária e "Cancelar" separado; o texto explica que a outra pessoa não saberá quem bloqueou.
7. Repita com "Sair e denunciar". **Esperado:** formulário com labels, erros associados e protocolo anunciado
   ao concluir.
8. Na conversa, abra "Opções da conversa" e repita 3, 4, 6 e 7 para Bloquear, Denunciar e Encerrar conexão.

### A11Y-04 · Minijogos

Duas pessoas (A com teclado e VoiceOver; B com qualquer configuração).

1. A e B aceitam o convite e entram na experiência. **Esperado:** A ouve a etapa, o objetivo e o prazo ao
   chegar ao `h1` da sala.
2. A lê as próprias pistas. **Esperado:** cada pista é texto; "só você vê" é anunciado.
3. A faz cada escolha só com teclado (Tab, setas nos rádios, Espaço ou Return). **Esperado:** nenhuma etapa
   exige arrastar, cor, som ou gesto; o estado selecionado é anunciado.
4. B escolhe enquanto A está no meio da leitura. **Esperado:** A recebe um anúncio curto da mudança, sem perder
   a posição.
5. Ative duas vezes seguidas "Escolher". **Esperado:** só uma escolha é consumida; o estado pendente é
   anunciado.
6. A e B concluem o jogo. **Esperado:** o resultado compartilhado é texto, sem nota; "Ir para a decisão" é a
   primária.
7. Compare com a sessão sem tecnologia assistiva. **Esperado:** mesmas regras, mesmas informações visíveis
   e a mesma privacidade.
8. Quando a versão tiver mídia (spec 6.5): entre na experiência sem tocar no Controle de som. **Esperado:**
   nenhum som. Ligue o som pelo teclado. **Esperado:** estado anunciado ("Som ligado"); cada fala aparece na
   transcrição com quem fala e, se for o caso, "voz editorial"; VoiceOver não lê a transcrição por cima do
   áudio sem pedido.
9. Ative "Reduzir áudio" e mude o volume de cada canal; recarregue a página. **Esperado:** preferências
   mantidas, som só depois do primeiro gesto; nenhum som perto dos marcos de prazo nem na decisão privada.
10. Com movimento reduzido e com WebGL desligado, abra um ambiente com 3D. **Esperado:** poster 2D com o mesmo
    texto alternativo; nenhuma ação exige girar ou arrastar.
11. Quando houver voz ao vivo: A liga o microfone, silencia B, desliga o próprio microfone e sai da voz só com
    teclado. **Esperado:** permissão do navegador pedida só no primeiro passo; cada estado anunciado; A continua
    na experiência e a termina pelo chat de texto.

### A11Y-05 · Chat e reconexão

1. Com VoiceOver, posicione o cursor no meio do histórico. Peça a B que envie uma mensagem. **Esperado:** a
   chegada é anunciada uma vez; o cursor e o foco não se movem.
2. Envie uma mensagem. **Esperado:** "Enviando" e depois "Enviada" são alcançáveis pelo cursor; não há anúncio
   de "lida".
3. Desligue a rede (Wi-Fi) e envie outra. **Esperado:** "Sem conexão" anunciado uma vez; a mensagem fica
   "Não enviada" com "Tentar de novo"; o texto não se perde.
4. Religue a rede. **Esperado:** "Conectado" ou o fim da faixa é anunciado uma vez; o histórico é reconciliado
   sem reler tudo.
5. Na experiência, derrube a rede por menos de 90 s e volte. **Esperado:** faixa "Reconectando" anunciada uma
   vez; ao voltar, a etapa é a do servidor e o foco não é roubado.
6. Deixe a tela do jogo aberta por um minuto com VoiceOver. **Esperado:** nenhum anúncio do relógio entre os
   marcos.
7. Carregue mensagens anteriores. **Esperado:** a posição de leitura é preservada.

### A11Y-06 · Layout e zoom

1. No Safari, com janela de 1280 px, aumente o zoom para 200% (Command-+). Percorra as telas centrais de
   [screens.md](screens.md). **Esperado:** nenhum conteúdo ou controle cortado ou sobreposto; sem rolagem
   horizontal.
2. Abra Desenvolvedor › Entrar no Modo de Design Responsivo e use 320 × 640 CSS px. **Esperado:** reflow em
   uma coluna; "Sair" da sala e a barra de navegação continuam visíveis e utilizáveis.
3. Em cada tela, percorra com Tab. **Esperado:** o foco sempre visível e nunca totalmente coberto por
   cabeçalho fixo, barra inferior ou toast.
4. Aplique espaçamento de texto ampliado (altura de linha 1,5, entre parágrafos 2×, entre letras 0,12em, entre
   palavras 0,16em) via folha de estilo do usuário ou bookmarklet. **Esperado:** nenhum texto cortado.
5. Meça o contraste de estados não cobertos pelo teste de tokens (texto sobre imagem, foco sobre componentes
   compostos, erro, desabilitado). **Esperado:** ≥ 4,5:1 para texto e ≥ 3:1 para componentes e foco.
6. No iOS e no Android da matriz, abra o teclado virtual no composer do chat e num formulário. **Esperado:** o
   campo focado e "Sair" continuam visíveis.

### A11Y-07 · Alvos e movimento

1. No Inspetor Web, meça a caixa clicável de botões, links isolados, rádios do jogo, itens da navegação e "×"
   dos diálogos. **Esperado:** ≥ 24 × 24 px (requisito) e ≥ 44 × 44 px nos controles de ação (meta do
   Duora); registrar cada exceção.
2. Ligue "Reduzir movimento" e recarregue. Abra e feche diálogos, receba um toast e uma mensagem. **Esperado:**
   sem transição animada; o estado final aparece imediatamente.
3. Com movimento normal, observe todas as animações. **Esperado:** nenhuma pisca mais de três vezes por
   segundo; nenhuma tem função de recompensa.

### A11Y-08 · Prazos e segurança

1. Na sala de espera, antes do convite, leia a tela com VoiceOver. **Esperado:** a duração do convite (60 s) é
   informada antes de ele chegar.
2. Receba o convite e não responda. **Esperado:** horário-limite em texto; ao expirar, mensagem neutra
   anunciada; a pessoa continua no evento.
3. No jogo, espere os marcos de 2 min e 30 s. **Esperado:** cada um é anunciado uma vez; entre eles, silêncio.
4. Deixe o prazo terminar no meio de uma escolha. **Esperado:** o que foi concluído é preservado; a tela de fim
   é anunciada; "Ir para a decisão" disponível.
5. Após o fim do prazo, procure "Sair", "Bloquear" e "Denunciar". **Esperado:** continuam disponíveis.
6. Deixe a conta inativa até o aviso de sessão. **Esperado:** aviso com a antecedência definida em
   [accessibility.md](accessibility.md) (nunca menos de 20 s),
   "Continuar conectada" com foco, extensão com uma ação.
7. Para cada linha da [justificativa de 2.2.1](accessibility.md#wcag-221-timing-adjustable-justificativa-por-fluxo),
   confira se as mitigações listadas existem na tela. **Esperado:** todas presentes; registre status por linha.

## Modelo de registro de evidência

Um registro por verificação, combinação e release. Relatórios ficam fora do repositório quando contêm
capturas com dados; no repositório entram só relatórios sanitizados (spec 21.3). Capturas e gravações usam
apenas contas sintéticas.

```markdown
## A11Y-0X · <nome da verificação>

- Release / commit: <tag ou SHA>
- Data: <AAAA-MM-DD>
- Avaliador(a): <nome>
- Ambiente: <local | homologação>, dataset <id>
- Combinação: <navegador e versão> + <tecnologia assistiva e versão> em <sistema e versão>
- Configuração: <zoom, Reduzir movimento, Tab no Safari, idioma do leitor>
- Escopo: <telas e estados percorridos>

| Passo | Resultado esperado | Observado | Status | Evidência |
|---|---|---|---|---|
| 1 | ... | ... | atendido | captura-01.png |
| 2 | ... | ... | falha | gravação-02.mov, issue #NN |

- Status da verificação: <falha | atendido | não aplicável com justificativa | não avaliado>
- Justificativa (obrigatória para N/A e não avaliado): <texto>
- Falhas abertas: <issues, com critério WCAG e severidade>
- Automação no mesmo commit: <link do job de E2E com axe; violações ou "nenhuma">
```

## Status

| Status | Quando usar | Exige |
|---|---|---|
| **Falha** | Algum passo não produziu o resultado esperado | Issue aberta com passo, critério WCAG, combinação e evidência |
| **Atendido** | Todos os passos produziram o resultado esperado nesta combinação e release | Evidência por passo |
| **Não aplicável com justificativa** | O conteúdo coberto pela verificação não existe no escopo (por exemplo, MFA desligado no release) | Justificativa escrita; revisão a cada release |
| **Não avaliado** | A verificação não foi executada (falta de dispositivo, tempo ou ambiente) | Motivo e plano; nunca contado como atendido |

Regras:

1. O status da verificação é o pior status entre os passos executados; "não avaliado" em qualquer passo
   obrigatório impede "atendido".
2. Ausência de violações no axe não muda nenhum status manual (spec 13.7).
3. Cada combinação da matriz tem status próprio; "atendido" em Safari + VoiceOver não vale para iOS.
4. Barreira em jornada essencial (cadastro, experiência, decisão, sair, bloquear, denunciar, exclusão) com
   status **falha** bloqueia o aceite T-23.
5. Pendências (não avaliado e N/A) são listadas no relatório do release, com responsável.
