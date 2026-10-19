# Escopo, invariantes, estados e glossário

Resumo operacional da [especificação](spec.md) (seções 1, 4.2, 4.3 e 12.2). Em caso de divergência, vale a
spec; a divergência é corrigida aqui no mesmo PR que a encontrar.

## Produto

O Duora organiza encontros entre duas pessoas adultas em eventos com rodadas e experiências cooperativas. O
sistema seleciona pares mutuamente elegíveis, conduz uma atividade curta, permite comunicação contextual e
coleta interesse de forma privada. Uma conversa persistente só nasce de interesse mútuo. Bloqueio, denúncia,
saída, proteção etária e revisão de incidentes fazem parte do fluxo principal, não de um complemento.

## Escopo da primeira entrega (spec 1.1)

| Capacidade | Base obrigatória | Fora da primeira entrega |
|---|---|---|
| Conta | Cadastro, confirmação de e-mail, login, recuperação, gestão de sessões e exclusão | Servidor OAuth próprio; múltiplos provedores sociais simultâneos |
| Perfil | Nome de exibição, faixa etária pública, região aproximada, descrição curta, fotos aprovadas e preferências privadas | Geolocalização precisa, importação de redes sociais e enriquecimento de dados |
| Eventos | Publicação administrativa, inscrição, lista de espera, check-in, rodadas e cancelamento | Marketplace de organizadores e eventos de terceiros |
| Pareamento | Elegibilidade bilateral, bloqueios, não repetição no evento, justiça de espera e rastreabilidade técnica | Ranking de atratividade, inferência de orientação e recomendação por processamento externo |
| Experiências | Uma experiência completa na primeira fatia; a segunda antes da expansão do piloto | 3D, voz, vídeo, scripts arbitrários e geração ao vivo no caminho crítico |
| Comunicação | Chat textual durante a sala e conversa persistente após interesse mútuo | Anexos, HTML, mensagens anônimas e criptografia ponta a ponta |
| Segurança interpessoal | Sair, bloquear, denunciar, encerrar conexão, revisão e recurso | Banimento permanente decidido só por classificador |
| Operação | Deploy repetível, telemetria, backups externos, restauração e controles administrativos | Alta disponibilidade prometida numa única VPS |
| Cobrança | Fronteira de integração documentada, sem dependência no piloto técnico | Pagamentos antes de validar o fluxo central |

Também fora do MVP: reconexão explícita depois de encerrar uma conexão (spec 7.1) e edição/exclusão de
mensagem (spec 7.2).

**Critério central de lançamento (spec 23.3):** duas pessoas adultas elegíveis concluem uma experiência,
decidem com privacidade e saem com segurança; o sistema continua consistente após retry, bloqueio concorrente,
reinício e recuperação.

## Público, idiomas e região (ADR-0026)

Decisões do dono de 2026-10-19 ([ADR-0026](../architecture/adr/0026-publico-idiomas-e-regiao-do-servidor.md)):

- **Público:** o principal **não é brasileiro**; os países do lançamento ainda não foram fixados. O Brasil é país
  provável (J-01); a aplicabilidade da LGPD, inclusive a quem está fora do Brasil, é a confirmar (J-02). As demais
  jurisdições são lacunas a confirmar com apoio jurídico
  ([data-map.md](../privacy/data-map.md#jurisdições-a-confirmar)).
- **Idiomas:** o produto inteiro funciona em **PT-BR e EN-US**: interface, e-mails, conteúdo das experiências,
  documentos voltados ao usuário e mensagens de erro. Nenhum texto voltado à pessoa existe só num dos dois.
- **Região:** servidor de produção em **Boston, EUA** (VPS Hostinger); backup no Backblaze B2 **US East**. Todos os
  dados ficam nos EUA.
- **Formatos:** instantes em UTC na API e no banco; apresentação no idioma e no fuso da pessoa, com o fuso do evento
  identificado; nada de fuso, moeda, data ou telefone fixos do Brasil.
- **Região do perfil:** hoje é a UF (só Brasil); a troca para país mais subdivisão está proposta no ADR-0026 e
  espera decisão.

## Invariantes (spec 1.2)

Nenhuma delas pode depender só da interface. A matriz de testes está em
[acceptance.md](../testing/acceptance.md).

| ID | Regra | Mecanismo principal | Teste |
|---|---|---|---|
| INV-01 | Só contas elegíveis e com requisito etário satisfeito participam | Política de autorização e verificação de estado no comando | T-01 |
| INV-02 | Uma pessoa ocupa no máximo uma sala reservada/ativa | Restrição única de participação não liberada | T-02 |
| INV-03 | Um bloqueio impede novo pareamento, novo envio e novas leituras protegidas entre o par | Guardas transacionais, autorização e invalidação | T-03 |
| INV-04 | Duas decisões positivas concorrentes criam no máximo uma conexão | Transação serializada para o par e índice único | T-04 |
| INV-05 | Pistas privadas e decisões alheias não chegam a um cliente não autorizado | Projeções por destinatário; nenhum estado integral no navegador | T-05 |
| INV-06 | Repetir um comando não repete seu efeito | Idempotência persistente, unicidade e versão esperada | T-06 |
| INV-07 | Notificação perdida não implica perda do estado confirmado | Banco autoritativo e recuperação HTTP | T-07 |
| INV-08 | Reiniciar web/worker não reinicia uma rodada nem duplica transições | Prazos persistidos, leases e transições condicionais | T-08 |
| INV-09 | Um jogo iniciado mantém sua versão de regras e conteúdo | Versões publicadas imutáveis, ligadas à sessão de jogo | T-09 |
| INV-10 | Uma decisão de moderação é rastreável e pode ser revisada | Registro de política, evidência, autor e recurso | T-10 |
| INV-11 | Restaurar backup não reativa silenciosamente contas excluídas ou sessões revogadas | Reconciliação e bloqueio de efeitos externos | T-11 |
| INV-12 | Nenhum agente de desenvolvimento tem autorização irrestrita em produção | Separação de credenciais, ambientes e gates | T-12 |

"Impedir após o bloqueio" significa negar operações cuja autorização ocorre depois do commit do bloqueio. Não
é possível apagar uma mensagem já recebida, recolher uma captura de tela nem cancelar bytes já em trânsito.

## Papéis e privilégio mínimo (spec 1.3)

| Papel | Pode | Não pode por padrão |
|---|---|---|
| Visitante | Ler apresentação, regras, acessibilidade e política de privacidade | Enumerar perfis, participantes ou eventos privados |
| Conta pendente | Confirmar e-mail, completar cadastro e resolver verificação etária | Entrar em sala ou ver dados de outras pessoas |
| Participante elegível | Inscrever-se, jogar, decidir, conversar quando autorizado, bloquear e denunciar | Consultar decisões negativas, pontuações internas ou evidências alheias |
| Moderador | Revisar casos atribuídos e aplicar medidas permitidas pela política | Acessar arbitrariamente todas as conversas ou operar infraestrutura |
| Administrador de produto | Gerir catálogo, eventos e configuração funcional | Conceder a si mesmo acesso irrestrito a dados sensíveis |
| Operador técnico | Gerir releases, saúde, recuperação e incidentes de infraestrutura | Usar acesso de banco como ferramenta rotineira de atendimento |
| Agente de desenvolvimento | Propor código e documentação em ambiente isolado | Ler banco real, segredos de produção ou evidências de moderação |

Uma equipe de uma pessoa pode acumular papéis, mas mantém contas, ações e trilhas de auditoria distintas.
Conta restrita ou suspensa acessa só o estado da própria conta, segurança e recurso (spec 8.2).

MFA é obrigatório para moderador, administrador de produto e operador técnico, e opcional (TOTP) para
participantes, pelos mesmos endpoints API-A11 a API-A15 ([ADR-0017](../architecture/adr/0017-mfa-por-papel.md)).

## Máquinas de estado (spec 4.2 e 12.2)

Todas as transições têm precondições e testes (spec 3.4).

| Recurso | Estados | Regras de transição |
|---|---|---|
| Conta | `PendingEmail`, `PendingEligibility`, `Active`, `Restricted`, `Suspended`, `DeletionPending`, `Deleted` | Nenhuma transição automática de suspeita etária para `Active` por decurso de prazo |
| Verificação etária | `Pending`, `VerifiedAdult`, `Rejected`, `Expired`, `ManualReview` | Indisponibilidade do fornecedor mantém a conta pendente; nunca aceitar `isAdult: true` do navegador (spec 12.2) |
| Evento | `Draft`, `Published`, `CheckInOpen`, `Running`, `Completed`, `Cancelled` | Publicação exige catálogo aprovado; cancelamento é auditado |
| Inscrição | `Waitlisted`, `Confirmed`, `CheckedIn`, `Withdrawn`, `NoShow`, `Completed` | Vaga ocupada e posição na fila mudam na mesma transação |
| Rodada | `Scheduled`, `Preparing`, `Inviting`, `Running`, `Closed`, `Cancelled` | Retomada reavalia horário e versão; não cria convites duplicados |
| Sala (`game_session`, [ADR-0016](../architecture/adr/0016-sala-e-sessao-de-jogo.md)) | `Invited`, `Ready`, `Active`, `Completed`, `Aborted`, `Expired` | Exige duas aceitações válidas antes do prazo; abortar libera reservas |
| Decisão | `Pending`, `Interested`, `NotInterested`, `Withdrawn`, `Expired` | Alterável até formar conexão ou expirar; cada mudança é versionada |
| Conexão | `Active`, `Ended`, `Blocked` | Encerramento não é revertido por decisão antiga |
| Upload | `Pending`, `Quarantined`, `Processing`, `Approved`, `Rejected`, `Deleted` | Nenhuma leitura pública antes de `Approved` |
| Denúncia | `Received`, `Triaged`, `UnderReview`, `Actioned`, `Closed`, `Appealed` | Recurso cria nova revisão, sem apagar a decisão anterior |

Uma sala concluída pode ter janela de decisão ainda aberta; a decisão não mantém a sala ativa nem reserva a
pessoa para a próxima rodada.

## Parâmetros iniciais (spec 4.3)

Todos são **hipóteses iniciais configuráveis**, armazenadas com versão e sujeitas aos testes. Uma sessão de jogo
recebe um snapshot da configuração ao nascer; alterações globais não mudam regras no meio de uma experiência.
Prazos de retenção ficam na política de privacidade (spec 12.4).

| Parâmetro | Valor inicial | Comportamento |
|---|---:|---|
| Participantes confirmados por evento | 100 | Máximo de 50 salas simultâneas; testar antes de admitir essa lotação |
| Rodadas por evento | 3 | Não repetir o mesmo par dentro do evento |
| Janela de uma rodada | 12 minutos | Inclui convite, jogo e intervalo; a próxima rodada não depende do timer do navegador |
| Abertura de check-in | 10 minutos antes | No início da rodada só entram participantes com presença confirmada |
| Prazo de convite | 60 segundos | Sem dupla aceitação, cancela o convite e libera reservas |
| Jogo ativo | Até 8 minutos | Deadline não ultrapassa o início da rodada + 9 minutos |
| Tolerância de reconexão | 90 segundos | Não estende indefinidamente o deadline da rodada |
| Janela de decisão | 24 horas após a conclusão | Expiração não é revelada como rejeição da outra pessoa |
| Mensagem | Até 2.000 caracteres Unicode, com limite adicional de bytes | Texto simples; contagem coerente entre frontend e backend; sem anexos |
| JSON de comando comum | Até 32 KiB | Upload tem limite próprio; limitar também profundidade e coleções |
| Foto | Até 5 MiB e 20 megapixels decodificados | Validar antes de processar; até 6 fotos aprovadas por perfil |
| Página de mensagens | Padrão 30; máximo 100 | Cursor opaco por conversa, ordenação estável |
| Idempotência HTTP comum | 48 horas | Respostas privadas mínimas; unicidades permanentes continuam existindo |
| Sessão normal | 30 minutos de inatividade; 12 horas absolutas | Reautenticação para ações sensíveis; sem "lembrar para sempre" por padrão |
| Sessão administrativa | 15 minutos de inatividade; 8 horas absolutas | MFA obrigatório (inclusive moderação, ADR-0017) e step-up para ações críticas |
| Concorrência inicial de processamento de imagens | 1 tarefa | Fila limitada; recusar ou suspender admissão quando saturada |

## Glossário

Um termo, um significado. A coluna "No código" dá o nome usado em tipos, tabelas e rotas; termos marcados como
"evitar" aparecem na spec como sinônimos e não devem ser usados em código nem em textos novos.

| Termo | No código | Significado |
|---|---|---|
| Conta | `User`, `users` | Identidade local de uma pessoa, com credenciais e estado (ver máquina de estados) |
| Sessão de login | `LoginSession`, `login_sessions` | Sessão autenticada revogável de uma conta num navegador; tem expiração ociosa e absoluta. Não confundir com sessão de jogo |
| Elegibilidade | `Eligibility` | Condição de uma conta para participar: estado `Active`, requisito etário satisfeito e ausência de sanção impeditiva |
| Verificação etária | `AgeVerification` | Procedimento que comprova a maioridade, com resultado mínimo, feito uma vez no cadastro e sem validade (ADR-0024). Evitar: "aferição de idade" |
| Perfil | `Profile` | Dados exibíveis da conta: nome de exibição, faixa etária, região aproximada, descrição e fotos aprovadas |
| Preferências | `Preferences` | Critérios privados de compatibilidade informados pela pessoa; nunca aparecem no DTO de outra pessoa |
| Evento | `Event` | Encontro publicado por administração, com horário, capacidade e rodadas |
| Inscrição | `Registration` | Participação de uma conta num evento; confirmada ou em lista de espera |
| Lista de espera | `Waitlisted` | Fila ordenada de inscrições sem vaga, promovidas de forma transacional e determinística |
| Check-in | `CheckIn` | Confirmação de presença dentro da janela antes da rodada |
| Rodada | `Round` | Intervalo do evento em que pares são formados e jogam |
| Candidato | `Candidate` | Participante que passou pelos filtros individuais da rodada (elegibilidade, check-in, sem sanção nem sala ocupada) e concorre a um par; leva só a espera, as rodadas sem par e as preferências |
| Par de contas | `UserPair` | Duas contas distintas sem direção, normalizadas (`user_low < user_high`), sem estado próprio. Usado pela seleção para o par escolhido, para bloqueios e para pares já formados no evento. Não é persistido; não confundir com `Pairing` |
| Par | `Pairing` | Registro persistido de duas contas selecionadas numa rodada, com o par normalizado (`user_low < user_high`); guarda a reserva do par, e a sala nasce do convite desse par |
| Reserva | `pairing_participants` com `released_at` nulo | Ocupação de uma pessoa por um par ainda não liberado; no máximo uma ativa por pessoa (INV-02) |
| Convite | `Invitation` | Chamada para uma sala, com prazo absoluto; exige duas aceitações |
| Sala | `/rooms/{roomId}`, com `roomId` = `gameSessionId` | Encontro de um par numa rodada, com chat de sala e experiência. É a sessão de jogo vista pela API, não uma entidade própria ([ADR-0016](../architecture/adr/0016-sala-e-sessao-de-jogo.md)) |
| Experiência | `Experience`, `experience_definitions` | Atividade cooperativa do catálogo (ex.: Rotas Improváveis). Evitar: "minijogo" em código |
| Versão de experiência | `ExperienceVersion` | Regras e conteúdo publicados e imutáveis de uma experiência |
| Sessão de jogo | `GameSession`, `game_sessions` | Instância de uma versão de experiência para um par numa rodada; nasce do convite e é a sala. Tem a máquina de estados da sala, estado de jogo e deadline. Não confundir com sessão de login |
| Comando de jogo | `GameCommand` | Envelope de uma ação de um participante, com `actionId` e versão esperada; deduplicado por `actionId` |
| Ação | `IGameAction` | Payload tipado de um comando de jogo, de um conjunto fechado por experiência (ex.: escolher uma opção) |
| Pista | `Clue` | Informação privada de um participante numa experiência; só chega ao próprio participante |
| Cartão | `ConstellationCard` | Pergunta leve com opções aprovadas em Constelação de Escolhas; as duas pessoas recebem o mesmo cartão |
| Pular | `SkipCard` | Ação de não responder a um cartão; o cartão passa sem mostrar a escolha de ninguém, e o resumo não indica quem pulou |
| Revelação | `CardOutcome.Revealed` | Cartão em que as duas pessoas confirmaram uma opção (dupla confirmação); só então as duas escolhas aparecem para o par |
| Cartão passado | `CardOutcome.Passed` | Cartão encerrado sem revelação, porque alguém pulou ou o prazo venceu; não guarda nenhuma resposta |
| Projeção | `ParticipantView` | DTO montado por destinatário, sem pistas nem decisões de terceiros |
| Decisão | `Decision` | Escolha privada de uma pessoa sobre interesse na outra após a sessão de jogo |
| Interesse mútuo | — | Duas decisões `Interested` válidas do mesmo par; cria uma conexão |
| Conexão | `Connection` | Relação entre duas contas criada por interesse mútuo; habilita a conversa persistente. Evitar: "vínculo" |
| Encerrar conexão | `EndConnection` | Ação que termina uma conexão ativa; não é desfeita por decisão antiga |
| Conversa | `Conversation` | Canal de mensagens; de sala (temporária) ou persistente (com conexão ativa) |
| Mensagem | `Message` | Texto simples numa conversa, com `clientMessageId` e sequência por conversa |
| Saída | `Departure` | Abandono imediato de uma sala, sempre permitido e sem formulário |
| Bloqueio | `Block` | Registro direcional com efeito bilateral: impede pareamento, envio e leituras protegidas entre o par |
| Desbloqueio | — | Remove o bloqueio; não restaura conversas, decisões nem salas |
| Denúncia | `Report` | Relato de uma pessoa sobre outra ou sobre um contexto, que gera um protocolo |
| Protocolo | `reportId` | Identificador que a pessoa usa para acompanhar a própria denúncia ou recurso |
| Evidência | `ReportEvidence` | Recorte autorizado vinculado a um caso, com acesso auditado |
| Medida de moderação | `ModerationAction` | Ação aplicada por moderação (restrição, suspensão, sanção), com política, motivo e autor |
| Recurso | `Appeal` | Pedido de revisão de uma medida própria; gera nova revisão sem apagar a anterior |
| Guarda | `user_guards`, `relationship_guards` | Linha usada como lock para serializar operações concorrentes sobre uma pessoa ou um par. Nunca representa consentimento |
| Coordenador | — | Registro (evento, rodada, relação) trancado para serializar um caso de uso |
| Outbox | `outbox_events`, `outbox_deliveries` | Intenção de efeito assíncrono gravada no mesmo commit do efeito de negócio |
| Lease | — | Posse temporária de um job por um executor |
| Fencing token | — | Número que identifica a posse atual; conclusão com token antigo é rejeitada |
| Chave de idempotência | `Idempotency-Key`, `idempotency_records` | Chave por principal e operação que faz a repetição de um comando devolver o mesmo resultado |
| Tombstone | `deletion_tombstones` | Registro pseudonimizado de exclusão, reaplicado após restore |
| Preservação | `retention_holds` | Retenção obrigatória ou aprovada, com escopo e vencimento, que suspende a exclusão de um recorte |
