# Reenvio explícito da confirmação — candidato de24/10/2026

Decisão4 de #9 sob delegação do titular. Extensão aditiva do fluxo API-A03, INV-01/02/05/06/08/12,
TM-06/TM-66/TM-67. Esta proposta não foi integrada, não envia e-mail real e depende de CI própria e revisão
de segurança antes de merge. A frente de transporte/outbox/suporte está com outro autor; não foi alterada.

## Comportamento

POST /api/v1/auth/email-confirmation-requests recebe e-mail e senha do cadastro. CSRF obrigatório; limites
de RegisterPerIp/RegisterPerAccount são compartilhados com cadastro, com429/Retry-After sem conta específica.
Corpos válidos recebem202 sem conteúdo/Location para conta inexistente, confirmada, senha errada, pendência
expirada, orçamento consumido ou reenvio aceito. O endpoint não cria sessão/conta nem altera senha/aceites.

Cada estado calcula o hash do código e verifica a senha ou isca. A iscaLazy pode calcular um hash adicional
no primeiro uso; não se afirma igualdade exata de trabalho ou distribuição. Um piso monotônico500ms com
jitter até100ms aplica-se aos202, depois de liberar a transação/conexão. Isso segue a mitigação explícita de
latência do login; não é alegação matemática de tempo constante ou prova de capacidade sob carga.
Banco indisponível conserva 503 e o fluxo de segurança existente; não é mascarado como 202. A conta ocupada
(fila por conta ou `lock_timeout`, 55P03) responde o mesmo 202 (issue #470): só a conta existente chega à trava,
e o e-mail inexistente sai antes, então um 503 ali diria que a conta existe (TM-06). O reenvio desiste sem gravar
código, stamp, orçamento nem outbox, e o motivo fica só num Warning sem e-mail nem id. Quem precisar do código
repete o pedido. `EmailConfirmationResendBusyTests` compara status, corpo sem `traceId` e headers (menos `Date` e
`Set-Cookie`) da conta pendente travada com os de um e-mail inexistente, e confere a outbox e o Warning.
O tempo também não distingue: o reenvio não espera o teto do login (`Identity:AccountLockTimeout`, 5 s), que
passaria do piso e diria pela demora que a conta existe; o jitter de 100 ms não esconde segundos numa amostra só.
A fila por conta e o `lock_timeout` esperam só `Identity:ShortWait:LockTimeout` (100 ms; validado entre 1 ms e
250 ms no boot), e a desistência cai dentro do piso. `EmailConfirmationResendTimingTests` compara a mediana de 11
amostras dos dois casos, com limite de 100 ms (medido: +10,8 ms; voltando ao teto longo, +975,8 ms com o teto do
login em 1,5 s). Pedido cujo trabalho passa do piso (hash lento sob carga) ainda pode diferir; o piso só iguala o
que fica abaixo dele.

Somente PendingEmail admissível e com senha correta pode usar o orçamento persistente de emissão3/h,
compartilhado com o cadastro e preservado após restart. Reenvio não renova CreatedAt/limite72h. A mesma trava
de confirmação relê a conta antes da decisão, serializa com recadastro/confirmação e grava código/outbox em
uma transação. Security stamp e concurrency stamp são trocados para invalidar links legados. Só o hash do
código fica em identity.user_tokens; a outbox existente guarda o envelope necessário ao envio.

Destino é o endereço armazenado, não uma variante digitada que coincida na normalização. Locale vem da
preferência gravada, com fallbacken-US. Não há acesso a SMTP/provider novo, mudança de consentimento, coleta
etária/documental, promoção de conta ou caminho novo de MFA. Recadastro existente continua alternativa explícita.

A SPA não dispara ao abrir a página: pede credenciais e clique. Sucesso e falha apagam a senha do formulário
e resetam o estado da mutação; gcTime0 evita conservar a mutação sem observador. Nada é colocado em URL ou
storage. Sucesso neutro recebe foco. A recusa não informa existência/status, nem bloqueia o caminho manual.

## Evidência necessária

Testes PostgreSQL/HTTP propostos em EmailConfirmationResendTests: senha/aceites/CreatedAt preservados;
link legado recusado e código novo válido; inexistente/confirmada/senha errada/expirada neutros sem nova
emissão/conta; orçamento compartilhado após restart e limite/Retry-After. Após review, o delta dirigido
substitui a corrida não determinística por duas ordens de commit reais viaHTTP, interpostas apósSELECT
e antes da materializaçãoEF; sincroniza dois resends antes dos locks na última permissão; intercala
recadastro com nova senha; e injeta falha antes do save da outbox para provar rollback de stamp/prova/orçamento
e retry. São12casosPG propostos, ainda não executados. A allowlist pública e a suíteCSRF incluem a rota.
Normalização/destino/IP reutilizam o parser, quotas e provas existentes de TM-67/AuthLimitsAcrossAccountsAndAddresses.

Testes DOM em CheckEmailPage.test.tsx: RED2falhas na página originalH233 (formulário/botão ausentes),
seguido de GREEN2/2 em1,30s e verificação final2/2 em1,41s no candidato. A primeira execução do candidato revelou um handlerCSRF ausente
somente na segunda fixture; corrigida essa fixture, os dois passaram. Cobrem nenhum envio automático,
credenciais+CSRF só no clique, mensagem neutra e senha apagada após202/429, sem submissão vazia.
Não constituem ensaio físico assistivo. Dependências já instaladas foram clonadas por copy-on-write para
diretório privado; sem instalação ou alteração da árvore de outro autor. HeapNode limitado a512MiB/umworker.
Typecheckapp e ESLint dos cinco arquivos alterados passaram; formatação dos arquivosTS/TSX passou.

OpenAPI aditivo foi preparado a partir dos schemas existentes e os tipos TypeScript gerados pelo CLI já
instalado, sem instalar dependências. O gatecontract deve conferir o OpenAPI efetivamente gerado pelo build;
esta proposta manual não é prova de saída idêntica. Testes .NET/PostgreSQL/build não foram executados no Mac
por restrição expressa de consumo; nenhum RED/GREEN foi atribuído a comandos não executados. Usar uma
qualificação hospedada da composição final, sem duplicar CI válida ou reduzir gates para publicar.

Revisão deve avaliar abuso/enumeração, piso sem conexão retida, limites após restart, concorrência e conteúdo
neutro antes de autorizar merge. Nada desta entrega aceita G2operacional/G3/G4/G5, sessão integral/T23,
entrega realGmail ou fecha #9/#79/#80 por inferência.

## Revisão independente

O head9ea90abbc5e6df6fc100acd9d87257725dd347cc recebeu parecer independente favorável à qualificação
hospedada, semP1/P2 demonstrado, SHA2563042467f7cb9cd460c9996af41f2263576ac1592785a7214b41d59d7704590a9.
A revisão não executou CI/testes e não autorizou merge. O delta posterior altera somente esta documentação
e os testes dirigidos acima; o produto permanece byteidêntico ao head revisado. Requer conferência dirigida
desse delta e todos os gates/backend/contract do head final antes da integração.
