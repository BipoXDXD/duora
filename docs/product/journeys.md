# Jornadas

Jornadas verificáveis da spec 4.1, com as rotas da API (spec 8) e os testes de aceite
([acceptance.md](../testing/acceptance.md)) que as cobrem. Cada jornada tem caminho de sucesso, falhas e
saídas obrigatórias; nenhuma é aceita só pelo caminho feliz (spec 22, M2). Estados e termos seguem
[scope.md](scope.md).

## Resumo (spec 4.1)

| Jornada | Fluxo completo | Falhas e saídas obrigatórias |
|---|---|---|
| Entrada | Criar conta → confirmar e-mail → concluir elegibilidade etária → aceitar versões dos termos → perfil mínimo | E-mail não chega, conta duplicada, análise pendente, falha no provedor, desistência e exclusão |
| Inscrição | Ver evento autorizado → conferir horário local → inscrever → confirmar vaga ou espera → receber lembrete | Lotação concorrente, cancelamento, alteração relevante de horário e perda de elegibilidade |
| Rodada | Check-in → aguardar → receber convite → aceitar → iniciar experiência | Pessoa ímpar, par incompatível, convite expirado, desistência e indisponibilidade do serviço |
| Experiência | Ler instrução breve → praticar ação → jogar → encerrar → decidir privadamente | Reconexão, ação duplicada, conflito de versão, acessibilidade, sair e denunciar |
| Conexão | Dois interesses válidos → criar conexão → abrir conversa persistente | Bloqueio concorrente, conta suspensa, decisão expirada e conexão já encerrada |
| Segurança | Sair/bloquear/denunciar → receber protocolo → acompanhar estado permitido → recorrer | Nenhuma obrigação de continuar na sala para produzir evidência |
| Privacidade | Solicitar exportação/exclusão → reautenticar → acompanhar tarefa → receber resultado | Links expirados, preservação restrita e dados de terceiros no histórico |

## Entrada

- **Rotas:** API-A01 a API-A04 (CSRF, cadastro, confirmação, login), API-P05/P06 (verificação etária),
  API-P01/P02 (perfil).
- **Regras:** cadastro e recuperação respondem de forma neutra, sem revelar se o e-mail existe; a confirmação
  de e-mail é `POST`, não `GET` acionável por scanner; o resultado etário nunca vem do navegador; sem
  fornecedor de verificação aprovado, a conta fica em `PendingEligibility` (spec 8.2, 12.2).
- **Saídas:** desistência e exclusão disponíveis em qualquer estado; falha do fornecedor mantém a conta
  pendente, nunca libera por autodeclaração.
- **Testes:** T-01, T-13, T-17.

## Inscrição

- **Rotas:** API-E01 a API-E04.
- **Regras:** contagem agregada, sem lista de participantes; uma inscrição por pessoa e evento; capacidade e
  lista de espera alteradas na mesma transação; horário exibido no fuso da pessoa, com o fuso identificado
  (spec 8.3, 13.6).
- **Saídas:** retirada libera vaga e promove a espera de forma determinística; cancelamento do evento é
  auditado e comunicado.
- **Testes:** T-01, T-06, T-28.

## Rodada

- **Rotas:** API-E05, API-E06, API-R01.
- **Regras:** check-in só na janela; pareamento bilateral e determinístico; convite com prazo absoluto no
  banco; a próxima rodada não depende do timer do navegador (spec 4.3, 5).
- **Saídas:** pessoa sem par recebe mensagem neutra de indisponibilidade, sem motivo que exponha preferências
  alheias; convite expirado libera reservas; após reinício, rodada passada é retomada ou encerrada pela
  política, nunca reiniciada (spec 5.2).
- **Testes:** T-02, T-03, T-08, T-18.

## Experiência

- **Rotas:** API-R02 a API-R04.
- **Regras:** o servidor valida ator, etapa e versão; cada participante recebe só a própria projeção; ação
  repetida não consome duas escolhas; a versão de regras e conteúdo fica fixa durante a sessão de jogo
  (spec 6.1–6.3).
- **Saídas:** sair funciona imediatamente e sem formulário; denunciar está disponível na sala; reconexão
  respeita a tolerância sem estender a rodada.
- **Aceite específico da primeira experiência (spec 6.2):** duas pessoas completam uma sessão; um terceiro não
  consulta a sala; A não obtém a pista de B antes da revelação autorizada; reenvio não consome duas escolhas;
  reinício durante a etapa de pistas preserva estado e deadline.
- **Testes:** T-05, T-06, T-07, T-09, T-18, T-23.

## Conexão

- **Rotas:** API-R05 a API-R07, API-C01 a API-C04.
- **Regras:** a decisão é privada; o cliente nunca recebe decisão negativa, horário de resposta nem status
  individual da outra pessoa; interesse mútuo é avaliado na mesma transação que grava a segunda decisão; a
  conversa persistente exige conexão ativa e ausência de bloqueio (spec 7.1, 7.2).
- **Saídas:** antes da conexão, a decisão pode ser alterada ou retirada dentro da janela; depois, a ação é
  encerrar a conexão; expiração não é revelada como rejeição.
- **Testes:** T-03, T-04, T-06, T-20, T-27.

## Segurança

- **Rotas:** API-R04 (saída), API-S01 a API-S06.
- **Regras:** bloqueio vale imediatamente e cancela interações pendentes; a outra pessoa recebe só
  indisponibilidade genérica; denúncia e bloqueio são ações separadas, com opção explícita de fazer as duas;
  desbloquear não restaura nada (spec 7.3, 12.3).
- **Saídas:** a pessoa não precisa continuar na sala para produzir evidência; conta suspensa mantém acesso ao
  recurso.
- **Testes:** T-03, T-10, T-20, T-26.

## Privacidade

- **Rotas:** API-D01 a API-D04, API-A13 (reautenticação).
- **Regras:** exportação só com dados legitimamente exportáveis do titular, sem decisões privadas nem
  evidências de terceiros; exclusão desativa e revoga imediatamente e executa o restante por etapas
  rastreáveis; arquivo de exportação expira em 24 horas (spec 12.1, 12.4).
- **Saídas:** link expirado orienta nova solicitação; preservação aprovada restringe só o recorte necessário.
- **Testes:** T-11, T-21.
