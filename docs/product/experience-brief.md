# Brief de experiência

Primeira entrega de design da [spec 13.1](spec.md#131-direção-visual-e-experiência): objetivos, tom, restrições,
tarefas prioritárias e situações de vulnerabilidade. Regras de produto vêm de [scope.md](scope.md) e
[journeys.md](journeys.md); em caso de divergência, vale a [spec](spec.md). As decisões visuais e de
interação que derivam deste brief estão em [design/system.md](../design/system.md),
[design/information-architecture.md](../design/information-architecture.md) e
[design/screens.md](../design/screens.md).

> **Status:** proposta para revisão. A identidade visual final e a validação com pessoas ainda não existem;
> veja [Pendências](#pendências).

## Objetivos

| # | Objetivo | Como se reconhece na interface | Como se mede (spec 13.6, 22.3) |
|---|---|---|---|
| O1 | Duas pessoas adultas concluem uma experiência e decidem com privacidade | A sala mostra o objetivo, a etapa e a próxima ação; a decisão fica numa tela própria, sem o resultado do jogo | Conclusão segura das tarefas; não o tempo de permanência |
| O2 | Cada pessoa sabe, a qualquer momento, o que a outra vê | Indicação textual de visibilidade junto de cada informação privada ("só você vê isto") | Teste de compreensão: "o que a outra pessoa sabe sobre sua decisão?" |
| O3 | Sair, bloquear e denunciar são localizáveis e imediatos | Ação "Sair" sempre visível na sala; segurança é área de primeiro nível | Teste de compreensão: "como você sai agora?" respondido sem ajuda |
| O4 | Prazos reais são informados sem pressão manipulativa | Horário absoluto e tempo restante em texto; avisos em marcos, não contagem a cada segundo | Ausência de relatos de pressa ou ansiedade atribuída ao relógio nas sessões de uso |
| O5 | Toda tarefa essencial funciona com teclado, leitor de tela, zoom e movimento reduzido | Ver [accessibility.md](../design/accessibility.md) | A11Y-01 a A11Y-08 e T-23 |

## Tom: acolhimento adulto, privacidade e curiosidade compartilhada

| Atributo | Significa | Não significa |
|---|---|---|
| Acolhimento adulto | Linguagem direta, cordial e sem infantilizar; trata quem usa como capaz de decidir | Gírias de app de namoro, emojis como pontuação, elogios genéricos |
| Privacidade | Dizer explicitamente o que é visível, para quem e por quanto tempo | Esconder regras em termos longos ou usar "talvez" sobre dados |
| Curiosidade compartilhada | O jogo é o assunto do encontro; convida a descobrir junto | Competição, placar, nota de compatibilidade ou recompensa por insistir |

**Personalidade visual** (Refactoring UI, cap. 1, "Choose a personality"): fonte sans neutra que não compete
com o conteúdo, raio pequeno e consistente, uma cor primária calma e tons quentes nos neutros. A cor de perigo
só aparece onde uma ação destrutiva é a ação principal da tela (confirmação de bloqueio ou exclusão).

### Voz e redação

- Frases curtas, voz ativa, segunda pessoa ("Você pode sair a qualquer momento").
- Verbo de ação nos botões, com objeto quando houver ambiguidade: "Sair da sala", "Bloquear pessoa", não "OK".
- Mensagens neutras quando a verdade pode ferir ou expor terceiros: "Esta conversa não está disponível", nunca
  "Fulana bloqueou você"; "A janela de decisão terminou", nunca "Não houve interesse" (spec 4.3, 7.3).
- Erros dizem o que aconteceu e o que fazer, sem culpar: "Não conseguimos enviar. Sua mensagem continua aqui."
- Nenhum texto de pressão: sem "corra", "últimas vagas", "fulano está esperando você".

## Restrições (spec 13.1)

| Restrição | Consequência de design | Verificação |
|---|---|---|
| Nada de dark patterns | Recusar, sair e excluir têm o mesmo peso de descoberta que aceitar; nenhuma confirmação constrangedora ("Tem certeza que quer perder sua chance?") | Revisão de redação; hierarquia de botões em [system.md](../design/system.md#hierarquia-de-ações) |
| Nada de ranking de aparência | Nenhuma lista ordenada por atratividade, curtidas, popularidade ou "mais vistos"; fotos não têm contadores | Inventário de componentes sem métricas sociais públicas |
| Nada de escassez artificial | Lotação aparece como fato ("Evento lotado; lista de espera disponível"), sem contagem regressiva de vagas nem "X pessoas vendo agora" | Revisão de cada card de evento e estado "lotado" |
| Prazo real não é pressão | Prazo mostrado como horário absoluto + tempo restante em texto; sem cor de alerta piscando; anúncio só em marcos | A11Y-05, A11Y-08 |
| Sem recompensa para insistir | Nenhuma animação, som ou "streak" ligado a voltar a uma pessoa ou repetir interesse | Inventário de movimento em [system.md](../design/system.md#movimento-proposto) |
| Recusar não é sabotado | "Não tenho interesse" e "Sair" nunca são texto cinza minúsculo, link escondido ou ícone sem rótulo | Alvo ≥ 44 px, rótulo textual, contraste medido |
| Sem estereótipos sobre quem usa | Nenhuma suposição de gênero, orientação, idade além do requisito etário ou objetivo do encontro; ilustrações sem casal-padrão | Revisão de conteúdo e imagens (spec 13.3, "Ilustração e fotografia") |
| Sem coleta disfarçada | O jogo Rotas Improváveis é fictício e nunca pede endereço, rotina ou localização real (spec 6.2) | Revisão do catálogo de conteúdo |

## Tarefas prioritárias

Ordem de prioridade para wireframes, protótipo e testes de uso; cada uma tem sucesso, abandono, erro e
alternativa segura em [journeys.md](journeys.md).

| Prioridade | Tarefa | Jornada (spec 4.1) | Sucesso visto pela pessoa |
|---|---|---|---|
| P1 | Sair de uma sala agora, sem formulário | Segurança | Volta à sala de espera ou ao evento em uma ação, com confirmação textual |
| P1 | Bloquear e/ou denunciar alguém | Segurança | Recebe protocolo; sabe que a outra pessoa não é avisada de quem bloqueou |
| P1 | Concluir uma experiência com a outra pessoa | Experiência | Vê o resultado compartilhado e chega à decisão privada |
| P1 | Decidir com privacidade | Conexão | Sabe que a escolha é privada, até quando pode mudá-la e o que acontece se for mútua |
| P2 | Criar conta e chegar a um perfil mínimo | Entrada | Entende cada estado de pendência (e-mail, elegibilidade) e o próximo passo |
| P2 | Encontrar um evento, inscrever-se e fazer check-in | Inscrição, Rodada | Vê horário no próprio fuso, situação da vaga e quando o check-in abre |
| P2 | Conversar depois de uma conexão | Conexão | Envia e recebe mensagens; distingue "enviando", "enviada" e "falhou" |
| P3 | Exportar dados ou excluir a conta | Privacidade | Acompanha a solicitação até o resultado |

## Situações de vulnerabilidade

O design parte do pressuposto de que qualquer pessoa pode, a qualquer momento, querer encerrar um contato sem
explicar o motivo. Nenhuma das situações abaixo exige continuar na sala para produzir evidência (spec 4.1,
"Segurança").

| Situação | O que a pessoa precisa | Resposta de design |
|---|---|---|
| Desconforto durante a experiência | Sair já, sem justificar | Botão "Sair" fixo no cabeçalho da sala; um toque abre diálogo curto em que "Sair agora" é a ação primária e o formulário de denúncia é opcional (A11Y-03) |
| Comportamento abusivo no chat da sala | Parar o contato e registrar | "Sair", "Bloquear" e "Denunciar" como ações separadas, com opção explícita de fazer bloqueio e denúncia juntos (spec 7.3) |
| Medo de retaliação | Que a outra pessoa não saiba quem bloqueou ou denunciou | Texto explicativo no diálogo de bloqueio: "A outra pessoa verá apenas que a conversa não está disponível" |
| Arrependimento depois de demonstrar interesse | Mudar de ideia sem constrangimento | Antes da conexão: "Alterar decisão" na tela de decisão; depois: "Encerrar conexão" na conversa e em Segurança (spec 7.1) |
| Conexão que ficou desconfortável | Encerrar sem confronto | "Encerrar conexão" e "Bloquear" no menu da conversa, sempre com rótulo textual |
| Uso em local não privado | Que a tela e as notificações não exponham nada | Notificações neutras, sem nome nem trecho (spec 7.2); nenhum rascunho íntimo salvo localmente (spec 13.2) |
| Conta suspensa ou restrita | Saber o motivo comunicável e como recorrer | Área Segurança continua acessível, com protocolos e recurso (spec 7.3) |
| Falha técnica no meio de um momento sensível | Não ficar presa numa tela | Estados "reconectando" e "sala encerrada" sempre trazem a saída e o caminho de volta ([information-architecture.md](../design/information-architecture.md#caminhos-de-recuperação)) |
| Deficiência, fadiga ou atenção dividida | Participar sem depender de reflexo, cor, arraste ou leitura rápida | Interações por botões e teclado; prazos com avisos; ver A11Y-04 e A11Y-08 |

## Pendências

- **Identidade visual final** (nome visual, matiz primário, fonte, ilustração): decisão do responsável pelo
  produto; os tokens atuais são provisórios (spec 13.3).
- **Revisão de uso com pessoas adultas** (spec 13.1, última linha da tabela): sessões com tarefas P1 e P2, com
  roteiro de compreensão da spec 13.6. Ainda não realizadas; nenhum item deste brief foi validado com
  participantes.
- **Testes com pessoas com deficiência**: parte da validação de uso quando organizados (spec 13.7); não
  substituem o [protocolo técnico](../design/accessibility-test-protocol.md).
