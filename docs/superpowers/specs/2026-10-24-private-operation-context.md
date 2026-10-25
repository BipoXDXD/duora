# Contexto de uma operação privada antes do aviso de sessão

Estado: emenda arquitetural para revisão delegada; nenhum código de protocolo implementado.
Predecessor da especificação `2026-10-24-session-expiry-warning.md`. Base funcional a compor:
H233 + PR241 final `a6d22e2`; não incorporar alterações no PR243 ou no ramo421 congelado.

## Problema e alcance

Uma tela validada como A pode continuar visível quando outra aba troca o cookie para B, sem aviso
BroadcastChannel recebido. O helper atual pede GET `/auth/csrf` e depois envia o DTO antigo. O token
recém-emitido pode ser de B. Capturar apenas uma geração JavaScript não detecta esse caso se o aviso
não chegou. Esta é uma hipótese causal de regressão a demonstrar em teste, não uma exploração
produtiva comprovada. O objetivo é impedir que intenção/dado preparado num contexto seja executado
sob outro, inclusive mesma pessoa com outra sessão. A autorização real permanece no servidor.

O predecessor altera o contrato privado de CSRF e a vinculação dos consumidores da SPA. O aviso,
os prazos e a proteção por suspensão são uma segunda fatia. Sem este predecessor aprovado e testado,
não certificar T14/T21 nem implementar meia proteção apenas por geração local.

Fronteira escolhida após a revisão: vincular ESCRITAS E LEITURAS privadas. Uma leitura privada sob
cookie B sem Broadcast também poderia receber dado B numa árvore/cache A. A emenda inclui a guarda
de leitura explícita e a adaptação do áudio nativo abaixo; geração local sozinha não dá essa garantia.
As mudanças continuam somente propostas neste desenho e precisam de revisão antes do plano/código.

## Fontes verificadas

Inspeção do ASP.NET Core v10.0.0, commit `7387de91234d3ef751fa50b3d1bfede4130213ff`:

- `DefaultClaimUidExtractor` prefere `sub`, depois `ClaimTypes.NameIdentifier` e issuer. Duora fornece
  NameIdentifier em `DuoraClaims.ForSignIn`; SessionId não participa dessa identidade antiforgery.
- `DefaultAntiforgeryTokenGenerator` verifica cookie/request security token, identidade e
  `IAntiforgeryAdditionalDataProvider.ValidateAdditionalData` antes de aceitar o conjunto.
- `DefaultAntiforgeryAdditionalDataProvider` emite e aceita somente dados vazios. Portanto não
  oferece vínculo adicional a uma sessão no estado atual.
- A interface oficial permite transportar/validar dados suplementares no token protegido existente.

Referências primárias: [extrator](https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Antiforgery/src/Internal/DefaultClaimUidExtractor.cs),
[gerador](https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Antiforgery/src/Internal/DefaultAntiforgeryTokenGenerator.cs),
[provider padrão](https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Antiforgery/src/Internal/DefaultAntiforgeryAdditionalDataProvider.cs)
e [interface](https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Antiforgery/src/IAntiforgeryAdditionalDataProvider.cs).

No Duora, StoreAsync atribui SessionId antes de concluir SignIn; StartSessionAsync substitui
HttpContext.User e só então emite antiforgery. RetrieveAsync deriva o estado atual de TouchAsync.
MeEndpoints retorna 200 durante inscrição MFA, mas senha temporária é barrada antes do handler com
403 PASSWORD_CHANGE_REQUIRED em SessionAuthorizationResultHandler. Não ampliar essa permissão.

## Alternativas e decisão proposta

| Alternativa | Consequência |
|---|---|
| Geração local e refresh CSRF atual | Não cobre cookie A→B sem Broadcast; rejeitada |
| Token de /me fixado, sem dados adicionais | Distingue pessoas, mas não a mesma pessoa com outra sessão; rejeitada |
| Prova antiforgery existente, vinculada à sessão e ao contexto privado | Escolhida; não cria criptografia, credencial ou prazo próprios |

Sem separar finalidade pública/privada, a SPA antiga poderia continuar obtendo um token sob B por
GET csrf e enviar intenção A. Para dar a garantia no conjunto publicado, a proposta exige separação
de finalidade e recusa explícita de leituras/escritas privadas antigas, com atualização controlada da SPA.
Isso é uma mudança comportamental a revisar e documentar, não uma compatibilidade presumida.

## Vínculo no servidor

Registrar implementação de IAntiforgeryAdditionalDataProvider. Dados suplementares versionados
contêm finalidade `public` ou `private` e o SessionId canônico já existente, somente dentro da
proteção criptográfica do framework. Anônimo usa marcador fixo sem SessionId. Não gerar nonce,
chave, hash de autenticação ou nova tabela; não alterar vida da sessão, stamp, MFA ou política.

Validar versão/finalidade/SessionId contra a identidade autenticada da requisição. Identidade
autenticada sem SessionId ou dados vazios/ilegíveis falham fechados. A identidade normal do
framework continua sendo verificada; o provider acrescenta o vínculo de sessão, não a substitui.

GET `/auth/csrf` e emissão depois de login/logout continuam emitindo finalidade pública no cookie
legível existente. Somente GET `/me` emite finalidade privada, no corpo da mesma resposta. Essa
emissão usa GetAndStoreTokens uma vez e conserva o cookie HttpOnly do framework; não gravar prova
privada no cookie legível, header ou storage. Não comparar textos de tokens para inferir identidade.
Não mudar o propósito de emissão no mesmo request depois que o framework já gerou um RequestToken.

Definir e testar metadados explícitos de finalidade, com lista fechada de rotas públicas. A escolha
não pode depender de um path fornecido pelo cliente, de prefixo amplo `/auth` ou apenas de AllowAnonymous.
Os webhooks conservam a isenção HMAC já auditada. Toda outra mutação privada exige finalidade privada.
Toda leitura privada de API também exige contexto, exceto o bootstrap /me explicitamente marcado.
Não aplicar essa exigência a assets estáticos, health, /auth/csrf ou rotas públicas explícitas.

| Fluxo | Prova aceita / resultado |
|---|---|
| Cadastro, confirmação/reenvio, login, solicitação/resgate de reset | Pública, vinculada à identidade presente ou anônimo; regras atuais mantidas |
| Escrita de conta/perfil/sessões/MFA/admin | Privada e da exata sessão atual; pública/legada é 403 antes do handler |
| Leitura privada JSON/bytes de API | Privada da exata sessão; prova inválida recusa antes do handler/corpo |
| Logout autenticado | Privada da sessão que o participante viu; não desconectar B com intenção antiga A |
| Logout já anônimo | Pública anônima permite comportamento idempotente existente |
| Troca de senha/MFA que cria nova sessão | Validar prova antiga antes da ação; sucesso encerra o contexto antigo, nova leitura /me é obrigatória |
| Reautenticação sem nova sessão | Mesma prova só se SessionId continuar igual e autorização atual permitir; não estender prazo absoluto |
| Webhook isento | Preservar assinatura HMAC e lista fechada existente |

## Leitura atômica e fase restrita

MeResponse 200 recebe objeto sessionContext tipado com UserId, SessionId, fase canônica
(`active` ou `mfa-enrollment`) e request token privado. UserId deve corresponder ao id externo
do mesmo corpo. Não aceitar mistura de duas respostas. A SPA valida o corpo inteiro, separa a prova para
um módulo em memória e devolve ao cache Me apenas os campos de identidade existentes. Uma resposta
tardia nunca instala prova em geração posterior. Campo ausente ou inválido impede montar toda a
área/operação privada, de leitura ou escrita; não recuperar por GET csrf nem usar cookie global.

Uma leitura /me da mesma tuple UserId+SessionId+fase mantém o handle e a geração ativos estáveis,
inclusive em refetch por foco/revalidação normal. O token aleatório não serve para comparar contexto.
Provas novas da mesma tuple podem ser usadas em operações FUTURAS; cada operação já iniciada conserva
seu snapshot imutável de prova/handle/geração antes do primeiro await. Nunca substituir a prova de
uma operação em voo pelo valor novo. Rotação para outro SessionId (mesmo UserId), fase diferente ou
proteção explícita invalida antes de reexpor DOM/cache. A árvore restrita não tem chave apenas UserId.

Uma resposta automática da mesma tuple não libera proteção persistente. O clique de revalidação
captura um novo epoch de autorização local; só a resposta desse clique, validada no mesmo epoch,
pode montar novamente a área. Não disparar leitura extra para comparar textos de tokens.

Tuple não substitui autorização atual: comparar também a projeção validada de status, emailConfirmed,
mfaEnabled e conjunto canônico de roles do mesmo /me. Mudança desses campos invalida o contexto antes
de reutilizar UI/cache, mesmo com UserId/SessionId/fase iguais. Não comparar locale/fuso como permissão.
Roles/projeção nunca vêm do cliente para autorizar o servidor: Touch lê privilégios atuais e os handlers
preservam suas verificações de política/conta/papel. Não confiar só no SID para detectar alteração de
privilégio nem alegar revogação instantânea de conteúdo já exibido sem evento/leitura posterior.

Para o 403 de GET /me especificamente PASSWORD_CHANGE_REQUIRED, acrescentar extensão sessionContext
da mesma forma canônica: UserId+SessionId+fase `password-change`+prova privada. SessionAuthorizationResultHandler já conhece essa
restrição depois de autenticação válida. O status/código e as permissões continuam iguais. Nenhuma
outra rota ou 403 recebe essa extensão. A SPA valida a resposta e a fase antes de habilitar o formulário
restrito. O MFA sem senha temporária continua usando /me200 e seus campos existentes. Respostas
401/503 ou outro 403 não criam contexto nem permitem operação privada. Preservar no-store e contrato OpenAPI.

SessionId já é listado ao titular por /me/sessions; a forma canônica nova permanece só no corpo privado
no-store do bootstrap. Não incluir SessionId, UserId, papel, ticket ou prova em headers, URLs, logs,
telemetria, BroadcastChannel ou storage. Prova privada é somente request token antiforgery protegido pelo framework; nunca é o
cookie de autenticação. Não registrar corpos de /me ou ProblemDetails contendo essa extensão.

## Uma intenção, um contexto, todas as esperas

Cada árvore privada recebe um handle opaco em memória vinculado ao /me validado. Callbacks/mutationFns
ficam vinculados a esse handle da árvore que gerou a intenção. Não obter contexto global novo depois
de uma validação assíncrona de formulário/arquivo: o DTO antigo não pode herdar o contexto B.

Capturar handle e geração antes do primeiro await da operação lógica. Conservar ambos durante toda
a cadeia: validação assíncrona, eventual leitura permitida, envio, parsing JSON/ProblemDetails e
callbacks de sucesso. O cliente aplica a prova desse handle a X-XSRF-TOKEN; nunca a substitui pelo
cookie legível mais recente. Não aceitar um header arbitrário do chamador como atalho.

Escritas privadas não fazem GET csrf nem refresh/retry automático por 403. Erro de contexto provoca
proteção/limpeza e revalidação explícita; o DTO e a intenção não são reenviados. Publicação de uma tuple
diferente ou proteção invalida/aborta os handles antigos. Checar geração antes de enviar e depois de
consumir qualquer corpo, inclusive erro; AbortError preservado e sem retry de leitura por erro de rede.

A ausência de Broadcast pode permitir que um POST antigo chegue ao servidor, mas a prova A com cookie
B ou sessão nova tem de produzir 403 antes da ação e nenhum efeito. Com geração local já alterada,
o teste mais forte exige zero POST. Não alegar zero rede no caso que só o servidor pode detectar.

As chamadas públicas usam helper e finalidade explícitos, separados das privadas. Inventariar todos
os consumidores e proibir leitura/escrita privada pelo helper público. Raw fetch/cliente gerado também deve
receber o handle correto ou recusar operação privada. A inspeção atual não achou fetch bruto privado
fora do cliente central, mas o plano precisa registrar inventário final após composição da base.

O cliente privado fica vinculado ao handle da árvore por closure. Chamadas GET/HEAD e de bytes usam
a mesma prova X-XSRF-TOKEN e snapshot anterior a await; não há leitura do cookie global como fallback.
O cliente bootstrap não adquire implicitamente um handle global para uma chamada antiga. Isso deve
ser expresso nas assinaturas de API/hooks; não basta anexar o contexto atual no ponto do fetch.

Consumidor nativo a substituir: RoomPage usa áudio com source=audio.url; GET privado
`/rooms/{roomId}/audio/{assetId}` exige EligibleParticipant e retorna WAV/no-store/nosniff. O elemento
não envia header escolhido pela SPA. O serviço já limita cada WAV PCM16 canônico a6MiB/60s,
1–2 canais e8–48kHz;24MiB é limite do REQUEST de catálogo, não orçamento de cache da SPA.

**CSP preservada:** infra/caddy/Caddyfile exige media-src 'self'; test_caddy_browser.cjs exige bloquear
blob media. Não adotar URL Blob em audio, nem acrescentar blob:/data: à CSP, URL com prova, service
worker ou exceção silenciosa da guarda. docs/operations/deploy.md já admite fetch+decodeAudioData
sob connect-src 'self'. Essa é a reprodução escolhida, sem pacote de codec ou nova permissão.

Player acessível usa fetch de bytes vinculado ao handle, somente após gesto explícito; mantém descrição
e transcrição. Não há preload/retry/renovação em background. Oferece botões de reproduzir/pausar/reiniciar,
som inicialmente mudo como hoje e controle explícito de som, progresso/seek por teclado e foco estável.
Não anunciar progresso a cada tick. Inicializar/resumir AudioContext no gesto, nunca na montagem.

Limitar corpo enquanto lê a6MiB (Content-Length não basta), rejeitar tipo/RIFF/PCM/duração/canais/rate
incompatíveis antes de decodeAudioData. Solicitar AudioContext a48kHz e verificar sampleRate efetivo
até48kHz antes de decodificar; se indisponível, manter transcrição e erro/retry explícito sem exceção
de CSP. Máximo representável de PCM Float32 é48.000×60×2×4=23.040.000bytes por cue; verificar também
AudioBuffer retornado. Manter somente UM cue em carregamento/memória/reprodução por PÁGINA, abortando
e descartando o anterior ao trocar. Esses limites de dados não certificam RSS total do navegador.

Orçamento de representações explícito: entrada6MiB; buffer de coleta6MiB sem lista ilimitada de chunks;
até duas cópias adicionais de entrada,6MiB cada, reservadas para trim/transferência/decoder; PCM final
até23.040.000B e reserva de mais23.040.000B para uma cópia transitória. Total reservado conservador
4×6.291.456 +2×23.040.000 =71.245.824B, sob teto de72MiB de representações contabilizadas. Não
produzir cópias fora dessa contabilidade. Cache retém no máximo UM PCM já incluído, nunca soma mais
um buffer final; descartar entrada/referências assim que consumidas. Não tratar esse orçamento como
hard limit do motor de áudio/RSS: buffers internos/GC dependem do navegador e precisam de evidência
própria para qualquer alegação física. Falta de reserva/suporte mantém transcrição sem fallback nativo.

Abortar leitura, parar fonte, desconectar nós/fechar contexto e descartar buffers em troca/proteção,
desmontagem ou nova seleção. decodeAudioData não tem cancelamento de CPU: coordenador da PÁGINA,
independente da árvore/epoch, impede segundo decode concorrente e mantém o slot/reserva ocupados
até o decode antigo terminar, inclusive após desmontagem. Nova árvore não recebe outra vaga. Sem
fila ilimitada; apresentar indisponibilidade temporária/retry por gesto, sem agendar novo HTTP.
Descartar resultado antigo por handle/geração e liberar contabilidade em finally.
Uma Promise tardia não pode tocar áudio, instalar buffer ou atualizar estado. Testar essa condição.
Não alterar arquivos da autoria237; compor seu head final e coordenar autoria do novo player antes de editar.

Fonte oficial adicional: [DefaultAntiforgery](https://github.com/dotnet/aspnetcore/blob/7387de91234d3ef751fa50b3d1bfede4130213ff/src/Antiforgery/src/Internal/DefaultAntiforgery.cs)
confirma que IsRequestValidAsync retorna true para GET/HEAD/OPTIONS/TRACE sem validar o conjunto.
ValidateRequestAsync valida-o também nesses métodos. A guarda de contexto em leituras chama essa API
explicitamente após autorização e antes dos handlers, inclusive para HEAD protegido. Tratar
AntiforgeryValidationException com403 neutro, sem expor texto interno. /me continua sendo o bootstrap
tipado que estabelece contexto novo, sem combinar prova nova com DTO/resposta antigo. Inventário
estático inicial:53operaçõesOpenAPI,15GETprivadoscandidatos, seisPOSTpúblicos de auth mais logout
condicional. Metadados/fallbacks reais e consumidores novos da composição final exigem conferência.

## Rotações e resultados de uma transição explícita

Fontes reais: CurrentSession.RenewAsync chama StartSessionAsync, conserva a política/limite absoluto
e cria outro SessionId. AccountSecurityService.RecordReauthenticationAsync mantém o SID e altera
provas recentes. StartMfaEnrollmentAsync muda security stamp e o adota na sessão atual, sem trocar SID.
RegenerateRecoveryCodes mantém SID e não demonstra troca/adoção de stamp: gera conjunto novo,
audit e aviso na mesma transação. Portanto há invalidações LOCAIS explícitas
independentes de comparar SID na resposta /me. Incorporar o inventário independente de12transições
INVENTARIO-TRANSICOES-2026.md, SHA2562629e8550a8a629bcae5c17f855de30bb5efe5e5d8ceb9b65bec3b25059d8293,
ao plano, conferindo os caminhos efetivos e idempotentes sem modificar a política MFA.

| Operação concluída | Ação local / servidor preservado |
|---|---|
| Login/logout | Invalidar contexto/cache anterior antes da interface nova; bootstrap público normal |
| Senha alterada, MFA confirmado/desativado | Revogações/stamp e nova sessão existentes mantidos; invalidar geração anterior; /me posterior estabelece interface nova |
| Reautenticação | Mesmo SID possível; invalidar operações antigas explicitamente após sucesso, nova leitura das provas, sem estender absoluto |
| Início/reinício de inscrição MFA | Mesmo SID/stamp adotado; descartar QR/chave anterior e operações anteriores; só o resultado validado desta intenção pode iniciar a nova apresentação |
| Regeneração de códigos de recuperação | Mesmo SID, sem alterar/adotar stamp; invalidar localmente operações/códigos antigos, handoff dos novos códigos só para o dono confirmado |
| Desativação MFA já desativado204 | Não supor rotação; preservar contexto se a leitura autoritativa conservar tuple/fase/projeção |
| Revogação de outra sessão | Não resetar a sessão atual por esse motivo; revogar a atual encerra seu contexto |
| Privilégio/status/MFA alterado por outro fluxo | Autorização atual no servidor; /me com projeção diferente invalida, ainda que tuple básica seja igual |

A leitura posterior a um sucesso é continuação da intenção explícita, nunca retry da escrita/DTO.
Erro/403 de contexto não faz essa recuperação automática. O Outlet antigo/QR desmonta antes de limpar.
Para preservar um segredo novo devolvido pela ação, usar transição de propriedade explícita: validar
o resultado enquanto seu snapshot original ainda vale, guardar só o mínimo em memória fora do cache
e proteger a apresentação até confirmar /me com o proprietário esperado. Falha ou proprietário
diferente descarta o resultado; não adotar secret de A sob contexto B.

Confirmação MFA retorna códigos de recuperação após criar SID novo. Acrescentar à mesma resposta
um identificador não portador `resultSessionId`, lido do principal novo, para correlacionar esse
resultado com o /me posterior (UserId esperado vem do snapshot da operação). Não é prova nem
autorizador; não é aceito do cliente no servidor. Apenas /me emite a prova privada. Isso evita gerar
dois tokens de finalidades diferentes no mesmo request já usado por StartSessionAsync. Os códigos
só aparecem se UserId e SessionId do /me validado coincidirem com o dono esperado; caso contrário
são descartados. QR do início de MFA exige o SID original e fase/projeção permitidas, sem herdar QR
antigo. Não persistir os segredos, reconsultá-los ou reenviar a ação se a continuação falhar.
Para códigos regenerados, o dono esperado é a sessão original, que não rotaciona; a resposta do DTO
compartilhado também informa seu resultSessionId. Somente esses resultados conhecidos podem usar o
handoff tipado de transição; não criar bypass genérico de geração para um endpoint ou callback.

Se um resultado único não puder ser apresentado com segurança (troca/rede/epoch), informar isso
explicitamente e explicar que a ação pode ter concluído. Não prometer recuperar/reexibir códigos
descartados nem gerá-los automaticamente: geração nova exige intenção explícita e as provas normais.
O plano deve tratar a tela de resultado sob o NOVO contexto validado, antes de navegar e sem publicar
setRecoveryCodes na mutationFn/árvore antiga como ocorre hoje. Não perder códigos silenciosamente.

Preservar contratos antigos de sucesso204 e retenções/limites de código; alteração do DTO de códigos
é aditiva e exige OpenAPI/tipos e testes normais próprios. Aviso entre abas não carrega identificador
ou segredo e continua sendo aceleração de invalidação, não a garantia de identidade da operação.

## Implantação e reversão

Nenhuma ativação produtiva neste trabalho. Publicar Web e SPA coerentes numa janela autorizada,
com assets versionados, no-store e verificação da versão servida. SPA antiga contra Web novo pode
ler apenas público/bootstrap: GET/HEAD e escritas privadas sem prova própria retornam403 e exigem
atualização; documentar essa transição em auth.md
e mensagens PT/EN. SPA nova contra Web antigo não monta área/operação privada de leitura ou escrita e exige versão
compatível. Não tratar o fallback antigo do aviso como cumprimento deste contrato.

Reversão deve ser do conjunto Web+SPA compatível. Não desligar apenas o provider ou aceitar dados
vazios temporariamente para obter verde: isso retiraria a garantia. Revisar o impacto no contrato,
fixtures CSRF existentes e rotas de bootstrap antes de implementar; não alterar seus limites para
contornar falhas. Issue93/T14/T21 exigem evidência no head composto e na versão publicada.

## Casos causais obrigatórios

1. Teste Node de caracterização reproduz o helper atual: intenção A, cookie troca para B sem Broadcast,
   GET csrf sob B, escrita antiga enviada com prova B a handler simulado. Isso demonstra o comportamento
   do helper; não alegar execução HTTP/PG da base sem o resultado hospedado correspondente.
2. Após a correção, A→B sem Broadcast: prova A + cookie/token público B dá403; zero handler/SQL de escrita.
   Repetir para GET privado: zero handler/corpo/cache B; bootstrap /me mantém contrato canônico próprio.
3. Mesma pessoa, SessionId novo: prova antiga também dá403; mesmo usuário/sessão permite ação normal.
4. Geração muda entre validação/refresh e POST: zero POST; contexto não é recapturado para o DTO antigo.
5. JSON e ProblemDetails atrasados após troca/proteção: nenhum cache, toast sensível, QR ou callback antigo.
6. Bootstrap anônimo, login, logout anônimo/autenticado, CSRF público, registro/reset/confirm/reenvio,
   senha temporária403, MFA200, rotação de sessão e HMAC isento preservam contratos respectivos.
7. Público/privado trocados, dados vazios/versão inválida/sid ausente, extensão em403 indevido e header
   arbitrário são recusados; no-store, cookie seguro e ausência de IDs/provas em saídas mantidos.
8. Fase restrita A→B desmonta QR/formulário antigo antes da leitura nova, mesmo com resposta em voo.
   Refetch da mesma tuple/fase não aborta intenção nem desmonta formulário/QR. SessionId novo com
   UserId igual invalida. Resposta automática da mesma tuple nunca libera proteção sem clique/epoch.
   Áudio sem gesto faz zeroHTTP/AudioContext; tamanho/PCM inválidos recusam antes do decode; um único
   cue/decode ativo por página mesmo após trocar árvore; resultado tardio após proteção não toca áudio.
   Mudança de roles/status/MFA com SID igual invalida; reautenticação/reinícioMFA invalidam por evento
   explícito; códigos novos com resultSessionId diferente do /me não são apresentados/adotados.
   Teclado/foco/transcrição/mute
   funcionam e a CSP continua bloqueando blob/data media com seus testes existentes intactos.
9. Backend/PG somente na CI hospedada necessária; no Mac apenas testes Node dirigidos e análise estática.
   Reaproveitar T14/T21/CSRF/contrato existentes, sem repetir gates válidos ou alegar RED não executado.

Depois da revisão desta emenda, escrever plano separado do predecessor e submetê-lo à revisão técnica
delegada antes do código. A segunda fatia de aviso conserva as duas exigências P2: geração lógica única
e proteção síncrona no retorno de suspensão com continuidade temporal incerta. Sem fechar #9/#79/#80
por revisão de desenho, CI genérica ou prova física inexistente.
