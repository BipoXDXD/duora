# Proposta de consolidação da issue 93

Data: 19/10/2026. Estado: proposta para revisão; implementação não realizada.

## Base e resultado pretendido

Checkout encontrado em `/Users/01bipo/Documents/Projetos/CSharp/Duora`. A main local
está em `5ea87255`, 234 commits atrás da main remota. A análise usa `92150065`, em
worktree separado na branch `codex/consolidation-93`. Não alterar `relatorios/`,
`wt-dep70/` nem outros worktrees existentes. Nenhum merge, deploy ou serviço pago.

A decisão vigente da issue 93 elimina verificação de idade. Confirmar e-mail continua
obrigatório; selfie serve somente para qualidade e prova de presença. Não solicitar
documentos, inferir idade ou exigir SMS. Manter sanções independentes, MFA por papel,
privilégio mínimo, proteção contra enumeração e pré-sequestro de contas.

## Seleção das alterações

| PR | Tratamento proposto |
|---|---|
| 76 | Aproveitar observador, monitor e integração OpenClaw isolados, seus testes e runbook. Não instalar, enviar Slack nem provisionar credenciais. Incluir seus recursos no orçamento se forem ativados. |
| 87 | Aproveitar orçamento conservador, testes e protocolo de carga. Aplicar apenas o delta próprio: a branch também contém documentação ancestral já integrada. |
| 88 | Não reintroduzir consumidor etário. Adaptar limite de tentativas, heartbeat, fencing e testes ao consumidor de selfie, se reutilizado. |
| 91 | Não aplicar integralmente: a main já usa en-US, fallback en-US e migração de preferência legada. A branch antiga restauraria fallback pt-BR e removeria a migração atual. Reaproveitar somente eventual cobertura de teste comprovadamente ausente. |
| 92 | Incorporar a decisão documental de idioma comum e opt-in bilateral. Não declarar o algoritmo de pareamento implementado por um PR documental. |
| 94 | Aproveitar confirmação por código e testes; excluir telas, contratos e ADR que mantêm maioridade obrigatória. Adaptar textos de e-mail e SPA ao novo estado de conta. |

O PR 89 também está fechado sem merge. Seus valores divergem do 87. Proposta:
orçamento de RAM conservador do 87 (PostgreSQL 2048 MiB; permanentes 5248 MiB),
com tetos de CPU de 1,5 para web/PostgreSQL e prioridade do caminho de requisição
inspirados no 89. CPU é teto, não reserva. Nenhum desses limites garante folga real
do host. Testes devem contar migrador, restore, monitor e collector por cenário,
sem presumir que 8 GB comerciais equivalem a 8192 MiB disponíveis. Carga e
MemTotal da VPS permanecem gates de homologação, sem acesso à produção nesta tarefa.

## Identidade, autorização e legado

Substituir a regra de participação etária por regra de conta: e-mail confirmado,
selfie aprovada e ausência de sanções impeditivas. A selfie obrigatória no cadastro
é a interpretação proposta da decisão e deve ficar explícita na revisão.

Separar estados da selfie: Pending, Capturing, Processing, Passed,
RecaptureRequired, Inconclusive, Expired. Qualidade ruim pede recaptura; presença
inconclusiva não vira classificação de menor, banimento nem aprovação automática.
Conta pendente de selfie não participa. Restricted, Suspended, DeletionPending e
Deleted nunca são liberados por resposta do fornecedor.

Não basta mudar `AgeEligibility.Evaluate`: revisar `Decide`, serviço de elegibilidade,
policy HTTP, consultas SQL, callbacks, worker, estado de conta e fixtures. Não
renomear VerifiedAdult para Passed: um resultado etário antigo não prova presença.

Migração expansiva: criar armazenamento próprio de verificações de selfie e manter
schema histórico até revisão de retenção. Desativar produção e consumo dos eventos
etários; callback antigo não pode ativar nem restringir conta. Encerrar solicitações
antigas com razão auditável e sem executar fornecedor. API antiga recebe resposta
de descontinuação documentada, sem aceitar resultados etários.

Contas legadas pendentes passam ao fluxo de selfie após confirmação de e-mail.
Contas ativas sem evidência de presença também precisam desse fluxo; nunca converter
o resultado adulto em prova de presença. Essa mudança exige comunicação antes da
release. Restrições exclusivamente etárias precisam de migração revisada baseada
na origem da restrição; não liberar indiscriminadamente Restricted. Se o registro
não distingue motivos, revisão humana identifica sanções independentes antes de
qualquer alteração. A idade não entra no novo cálculo de elegibilidade.

Não aplicar DROP, apagar evidência ou executar migração destrutiva. Rollback pausa
novos cadastros/participação afetada e preserva dados; não reativa silenciosamente
o requisito etário revogado. Ensaiar upgrade/rollback com dados sintéticos.

## Código por e-mail

Manter proposta do 94: oito dígitos ASCII, geração criptográfica, 15 minutos,
hash vinculado ao cadastro/security stamp, consumo único e confirmação com senha
do cadastro correspondente. Preservar locks, releitura e commit atômico. Código
antigo, expirado, usado, substituído ou acompanhado de senha errada não confirma.

Reenvio deve invalidar código anterior, ter cooldown e orçamento por conta/IP,
sem reiniciar ilimitadamente o limite de tentativas. Não herdar automaticamente
`CreatedAt` como relógio do código se houver reenvio: armazenar emissão/expiração
explícitas. Respostas neutras e custo comparável para cadastros existentes/inexistentes.
Links legados só durante janela documentada e sem fallback quando já existe código.
Outbox não registra código em logs; revisar proteção do payload e sua retenção.

## Fornecedor proposto: AWS Rekognition

Preferência técnica para avaliação: Face Liveness com componente oficial Amplify
React e adaptador .NET no Infrastructure. A captura envia vídeo ao fornecedor;
o backend cria sessão e consulta resultado autenticado. O retorno do navegador é
apenas sinal para consulta, nunca autoridade para aprovação. Vincular sessão à
conta, nonce e tentativa; rejeitar troca de conta, replay e resultado tardio.

Avaliar qualidade do frame de referência com DetectFaces usando exclusivamente
`Attributes=[DEFAULT]`: essa seleção retorna geometria, confiança, pose e qualidade.
Proibir `ALL`, `AGE_RANGE`, comparação facial, coleções e busca de identidade.
Não pedir nem persistir idade, gênero ou emoções. Separar presença de nitidez,
iluminação e enquadramento. Limiares devem ser versionados e calibrados com a matriz
de dispositivos; configuração ausente não pode liberar produção nem usar valor
permissivo inventado.

Outbox leva somente identificadores. Reutilizar as garantias do 88: tentativas
limitadas, renovação de leases, fencing, idempotência por tentativa e ACK após
persistência. Sessão do fornecedor tem prazo curto; priorizar criação após usuário
pronto e consulta do resultado antes de expirar. Polling persistente com backoff,
limites e deadline, sem depender de webhook que esse fluxo não oferece. Callback
local da SPA não substitui autenticação servidor-servidor.

Credenciais de captura temporárias e de privilégio mínimo; nada estático no bundle.
Validar os limites reais de escopo IAM antes da ativação. Não conceder acesso público
irrestrito para criação de sessões pagas. Novas dependências exigem revisão de
licença, lockfile e supply chain; nenhuma foi adicionada nesta proposta.

Alternativas: fornecedor hospedado especializado pode reduzir integração de SDK,
mas exige outra avaliação contratual; adaptador genérico/fake só serve a teste e
não satisfaz a exigência de fornecedor real. Não substituir captura por upload.

## Retenção proposta e acessibilidade

Duora não grava vídeo/selfie em disco, storage, outbox, logs, tracing ou foto pública.
Sem OutputConfig/S3 e com AuditImagesLimit=0. Frame usado para qualidade apenas em
memória e descartado após resultado. A expiração da sessão em três minutos torna
dados indisponíveis pela API; NÃO comprova eliminação em todos os sistemas AWS.

Antes da ativação: verificar termos de processamento/retencão, região suportada,
opt-out de uso para melhoria de modelos e cobertura da conta. Não afirmar que a
configuração do cliente garante retenção zero no fornecedor. Proposta de retenção
local: estado, versão da política e data de aprovação enquanto a conta existir;
identificadores técnicos/resultados transitórios por no máximo 24 horas; contadores
de abuso por 24 horas, sem imagens. Revisão de privacidade aprova esses prazos e
as exceções de auditoria antes da release.

PT-BR/en-US, foco e mensagens anunciadas, teclado, instruções textuais, câmera
negada/ausente e cancelamento com retorno seguro. Preferir desafio sem luzes se
validado para o caso de uso; isso não garante acessibilidade de movimentos de
cabeça. Validar alternativa assistida sem documento nem idade antes de lançamento;
não presumir que suporte ou recaptura resolve toda deficiência. Nunca conceder
aprovação automática por impossibilidade de usar a câmera.

## Contratos, documentação e validação

Novo ADR substitui as partes etárias do 0024 e a proposta anterior do 94, preservando
histórico. Revisar INV-01/T-01, API-A02/A06, API-P05/P06, spec 4.1/8.2/12.2,
scope, modules, auth, telas, mapa de dados e aceite. Regenerar OpenAPI e cliente;
rotas e DTOs não expõem VerifiedAdult, NotAdult ou MinorSuspected no fluxo novo.
Preservar INV-02 a INV-12, sanções, bloqueios, segurança de mídia e pareamento.

Testes obrigatórios da implementação: concorrência de confirmação/reenvio e
cadastro substituído; replay; atraso/indisponibilidade do fornecedor; quotas;
resultado falso enviado pelo navegador; sanção concorrente; qualidade ruim;
expiração; lease perdido; orçamento de tentativas; migração de todos os estados
legados; ausência de dados sensíveis em logs e outbox; nenhuma chamada com idade.
PostgreSQL real via Testcontainers, sem SQLite/InMemory. SPA com Vitest e Playwright,
mais câmera e tecnologias assistivas reais antes da ativação. Sandbox do fornecedor
é validação separada dos mocks. Não enviar dados de participantes aos testes.

Entregas revisáveis separadas: identidade/selfie, infraestrutura/observação e
política linguística. Não alterar workflows, CODEOWNERS ou permissões como efeito
colateral. Nenhuma integração ou implantação antes de revisão humana e gates.

## Fontes verificadas

- https://github.com/BipoXDXD/duora/issues/93
- https://github.com/BipoXDXD/duora/pull/94
- https://github.com/BipoXDXD/duora/pull/90
- https://docs.aws.amazon.com/rekognition/latest/dg/face-liveness-programming-api.html
- https://docs.aws.amazon.com/rekognition/latest/APIReference/API_CreateFaceLivenessSession.html
- https://docs.aws.amazon.com/rekognition/latest/APIReference/API_DetectFaces.html
- https://docs.aws.amazon.com/rekognition/latest/dg/face-liveness-faq.html
- https://docs.aws.amazon.com/rekognition/latest/dg/recommendations-liveness.html
- https://ui.docs.amplify.aws/react/connected-components/liveness

Esta proposta não é contratação, implementação, validação de limiares, avaliação
jurídica nem aprovação de produção. O responsável autorizou preparar a proposta;
fornecedor, retenção e migração precisam da revisão deste documento.
