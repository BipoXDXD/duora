# Aviso e proteção da sessão na SPA

Estado: desenho arquitetural para revisão técnica delegada; **produto ainda não implementado**.
Autor: frenteCF de #9. Base funcional a compor: H233 com PR241 final a6d22e2.
As ordens vigentes autorizam execução/decisões/revisão delegadas e não autorizam merge fora da fila.

Predecessor obrigatório: `2026-10-24-private-operation-context.md`. A revisão do desenho encontrou
um contexto adicional que geração JavaScript isolada não cobre: cookie troca A→B sem Broadcast e
mesma pessoa em nova sessão. O predecessor precisa de revisão/plano/testes próprios antes do aviso.

## Objetivo e critérios

Implementar a decisão1 de #9: aviso antecipado e retorno seguro com estado recuperado do servidor,
preservando inatividade30min/absoluto12h de participante e15min/8h de administração. Nenhum limite aumenta.
Não introduzir polling/heartbeat para manter autenticação. Não colocar rascunhos, credenciais ou histórico
privado em URL/storage. A avaliação física de leitor de tela e WCAG integral continuam com provas próprias.

INV-01/02/05/08/12; fluxos API-A04/A08/A13; T-14/T-21/T-23. Servidor continua decidindo toda autorização.
Esta fatia não altera MFA, SMTP/outbox, moderação, perfis, países, migrações ou operação regional.

## Opções consideradas

| Opção | Resultado |
|---|---|
| Prazo obtido apenas de /me | Outras operações renovam idle no banco e deixam esse prazo desatualizado; cria avisos prematuros |
| Metadados da sessão nas respostas protegidas + relógio local relativo + proteção da tela | Mantém o aviso vinculado à atividade autenticada efetiva sem nova consulta automática; escolhida |
| Inferir prazo por teclado/clique | Não representa o relógio/estado do servidor ou outra aba e produz falsa autoridade |

## Prazos efetivos no servidor

LoginSessions.TouchAsync já atualiza idle com least(now+janela,absolute) e recusa sessão expirada/revogada.
Guardar o mesmo now em variável, acrescentar idle_expires_at ao RETURNING e expor ObservedAt/IdleExpiresAt
como propriedades transitórias opcionais de TouchedSession. Preservar seu construtor atual. Sem coluna ou
consulta nova. Os limites existentes continuam sendo a única fonte da janela.

PostgresTicketStore.RetrieveAsync transporta esses dois instantes em claims privados de estado, junto do
AbsoluteExpiresAt já existente. DuoraClaims deve removê-los antes de proteger/renovar um ticket e sempre
reconstituí-los do Touch. Propriedades opcionais no corpo de SessionState preservam o construtor atual.
Nenhum claim novo concede papel ou autorização e nenhum instante vindo do cliente é aceito.

Adicionar middleware após UseAuthentication e antes de UseAuthorization em DuoraWebApp. Somente para
endpoint protegido de /api/v1 com identidade autenticada e metadados completos, registrar OnStarting com três headers numéricos:

- X-Duora-Session-Observed-At: instante do Touch em milissegundosUTC;
- X-Duora-Session-Idle-Remaining-Ms e X-Duora-Session-Absolute-Remaining-Ms: durações restantes
  naquele mesmo instante, limitadas pelas janelas existentes.

Não expor UserId/SessionId em headers: proxies podem registrar headers de resposta. A correlação da SPA
usa a geração local dos pedidos e a identidade já retornada pelo corpo validado de /me; nenhum identificador
novo em header é necessário. A tuple canônica UserId+SessionId+fase vem somente no corpo privado de /me
conforme o predecessor; sua prova é a do framework e permanece só em memória.

Não emitir em rotas anônimas, health, webhooks, assets ou para sessão não autenticada. Uma resposta privada
403/401 por reautenticação pode ter sessão válida: conservar os metadados desse Touch, sem confundi-la com
expiração. Não remover Cache-Control:no-store. Não expor cookie, chave, ticket, senha, códigoMFA, IP ou
papel nesses headers. Não modificar CORS, permissões ou credenciais.

## Relógio e fronteiras na SPA

Criar módulo shared puro sem importação de API/features: estado em memória, parser dos três headers,
relógio monotônico injetável, geração de identidade e assinaturas para useSyncExternalStore. Rejeitar campos
malformados/inseguros, durações negativas ou idle>absolute e janelas acima dos limites máximos existentes.
Metadado ausente/malformado não aumenta prazo conhecido; respostas observadas fora de ordem são descartadas.
O relógio absoluto local não decide expiração. Entre respostas de uma mesma geração, a estimativa do limite
absoluto só pode ficar mais conservadora. Revalidação explícita estabelece uma nova leitura autoritativa.

Uma operação lógica captura o handle privado e a geração antes do PRIMEIRO await e conserva ambos
por validação, CSRF quando permitido, envio e parsing de sucesso/ProblemDetails. Não recapturar geração
nova para um DTO antigo. Alteração de geração entre passos exige zero POST. Sem aviso local da troca,
a prova privada do predecessor impede a escrita no servidor sob cookie de outra sessão/pessoa.
O clienteHTTP central conserva essa geração e o instante monotônico de início antes de enviar. Ao receber,
usa durações do servidor e desconta o tempo total da requisição de forma conservadora. Validar a geração
antes e depois de ler JSON/terminar comando, impedindo que sucesso tardio iniciado antes da proteção/troca
de pessoa repovoe caches. Cada geração tem um AbortController: trocar/proteger aborta também corpos em voo,
combinando o sinal da geração ao do chamador. Cancelamento por geração deve resultar em aborto sem retry,
preservando shouldRetryQuery; não virar ApiNetworkError que provoque refetch de /me em repouso. Conferir
consumidores de fetch bruto e compatibilidade dos alvos antes de escolher o combinador de sinais; nenhum
consumidor privado pode contornar essa fronteira. Não adicionar dependência ou API de browser incompatível.

Adicionar captura de contexto de resposta opcional por chamada de requestJson. O contexto corresponde à
mesma resposta/corpo e contém geração, instantes monotônicos e os metadados numéricos; não se usa um slot
compartilhado de “último /me”. fetchMe só confirma identidade/contexto e leitura quando meSchema valida o corpo.
Diferença de UserId, SessionId ou fase invalida a geração anterior; refetch da mesma tuple conserva o handle
ativo. Nenhuma resposta automática desbloqueia proteção persistente. Login/logout/rotação de credencial e o aviso
existente entre abas invalidam estado/pedidos da geração antiga; não transmitir novos dados por canal ou storage.

Fases autenticadas restritas (senha inicial/MFA) podem ter /me403 e precisam de proteção também. Os headers
privados confiáveis podem iniciar a contagem, mas não substituem o contexto canônico/prova do predecessor
para habilitar o formulário restrito. Um403 de restrição com contexto válido pode ser revalidado explicitamente sem login artificial.
Qualquer mudança posterior de pessoa pelo /me validado conserva a limpeza já existente de T21.
Mudança de identidade usa fase de verificação transitória: desmonta também o Outlet restrito de senha/MFA
antes do resetQueries existente, sem preservar sua chave local durante a espera. /me novo validado ou403
restrito atual permite montar a fase da nova identidade. Expiração tem trava persistente diferente e só
um clique explícito a libera. Não reutilizar lastRestriction para mostrar QR/chaveMFA da pessoa anterior.

## Aviso e proteção da tela

Um componente da área autenticada apresenta aviso PT-BR/EN-US a60s do menor prazo efetivo, sem anúncio
contínuo a cada segundo. Timers apenas alteram estado local; visibilitychange/focus recalculam o prazo.
Antes de hidden/pagehide/freeze, ocultar sincronamente a área privada/QR. Quando a continuidade do
relógio monotônico não puder ser comprovada no retorno (incluindo performance pausado durante sono e
Date ajustado), entrar em proteção persistente ANTES de reexpor DOM/QR e retomar observadores. Tratar
a incerteza conservadoramente; não prometer detectar todo sono por comparação entre dois relógios.
FocusManager, useMe e callbacks resetQueries não podem produzir HTTP automático enquanto protegida.
Retorno incerto exige clique de revalidação; a proteção local não afirma revogação no servidor.
Não fazer pedido automático quando o timer vence. Os controles devem funcionar por teclado e ter foco
previsível. Ação de continuar/revalidar é sempre um clique explícito que consulta /me.

Ao atingir o prazo local, desmontar a área privada e mostrar uma tela de proteção neutra. Não afirmar que
o servidor revogou a sessão: outra aba pode ter feito atividade. Após desmontar, cancelar/remover consultas
privadas e mutações, conservando o observador /me sem disparar refetch. Invalidar a geração dos pedidos
anteriores impede que respostas tardias reconstruam esses dados. Nenhum rascunho privado é preservado.

O clique explícito revalida /me: sessão válida retorna ao contexto da identidade atual e busca estado do
servidor;403 de fase restrita com metadata válida volta à fase própria;401 segue o retorno seguro existente
para login e a limpeza de identidade existente. Falha de rede/5xx ou respostas antigas não desbloqueiam a tela.
O estado protegido por expiração é persistente: observar uma resposta nova nunca o desbloqueia sem esse clique/verificação. Reautenticação
não estende o limite absoluto da mesma sessão. Entrada nova exige as provas normais de senha/MFA. A proteção
local não revoga outra aba nem mantém a sessão viva silenciosamente.

Servidores antigos sem headers conservam o comportamento de401 do aviso até a atualização compatível,
mas ausência do contexto privado do predecessor impede toda operação privada de leitura/escrita na SPA nova. Não usar fallback
do aviso para contornar essa fronteira.
Depois de haver prazo conhecido, ausência de headers não desliga a proteção. Validar no release final que
Web/SPA servidos incluem esse contrato; não declarar T14/T23 completo por fallback de compatibilidade.

## Arquivos e responsabilidade

- backend/src/Duora.Infrastructure/Identity/LoginSessions.cs: instantes do Touch, sem mudar política.
- backend/src/Duora.Web.Identity/{DuoraClaims,PostgresTicketStore}.cs: estado efêmero derivado do banco.
- backend/src/Duora.Web/Security/SessionDeadlineHeaders.cs e DuoraWebApp.cs: headers privados sem consultas.
- frontend/src/shared/session-deadline.ts e testes: parser, tempo, geração, correlação e ausência de storage.
- frontend/src/shared/api/{api-client,index}.ts e testes: fronteira de resposta e instantes da requisição.
- frontend/src/features/session/{session-api,session-hooks,RequireSession}.tsx/ts e novo componente:
  vincular /me, aviso, proteção e revalidação explícita. Conferir nomes reais antes do plano de implementação.
- frontend/src/app/{App,query-client}.tsx/ts: integrar limpeza existente de identidade, preservando T21.
- catálogos PT-BR/EN-US e testes dirigidos; docs/testing/session-expiry.md com limites/evidências reais.

Não tocar arquivos de transporte/outbox do autor238 ou alterar os heads em revisão243/421. Dependência241
mantém o retorno sem query privada; não incorporar uma cópia divergente do helper.

## Provas antes da integração

1. HTTP/PostgreSQL com FakeTimeProvider: headers/durações corretos de normal/admin sem IDs, limite absoluto imutável,
   novo Touch renova apenas idle, expirado/revogado sem metadata, rotas anônimas sem header, no-store intacto,
   claims privados não persistidos/forjáveis e nenhum SQL adicional.
2. UnitNode: relógio do computador deslocado, atrasos de rede, resposta fora de ordem, deadline malformado,
   troca A→B, rotação de sessão, contexto /me emparelhado à resposta e descarte após leitura de JSON
   iniciada na geração anterior. Nenhum identificador de usuário/sessão ou conteúdo privado em headers, logs ou BroadcastChannel.
3. DOM: aviso60s/teclado/foco, suspensão longa com performance pausado/Date deslocado e proteção antes
   de DOM/QR/observadores, nenhum HTTP automático ao aguardar ou proteger, revalidação só no clique,
   privateOutlet desmontado antes de limpar, pedido em voo incapaz de repovoar, outra aba/sessão nova,
   falha de rede sem reabrir tela e401 com retorno sanitizado.
4. Reusar T14/T21 e testes de limites/reautenticação existentes; executar novos testes leves com dependências
   existentes. Backend/PG/build somente em CI hospedada necessária, sem SDK/Docker/carga no Mac.
5. Revisão independente do head final, nove gates próprios, integração pela fila/expectedhead. Sem alegar
   RED/PG/AT físico ou relógio/capacidade produtivos sem resultados correspondentes.

## Limitações e revisão exigida

Timers podem atrasar em aba suspensa; o servidor continua recusando ações expiradas e o retorno à visibilidade
deve proteger a tela antes de retomar trabalho. Não prometer instante físico ou conforto assistivo sem prova.
Avaliar explicitamente cancelamento/limpeza com mutações em voo, isolamento entre usuários e fallbacks antigos.
Não introduzir nonce de autenticação, credencial nova, criptografia própria ou renovação silenciosa.
O predecessor reutiliza o request token antiforgery e sua extensão oficial, conforme emenda revisável.
Esta revisão aprova desenho; plano escrito e método de execução ainda precisam de revisão antes do código.
