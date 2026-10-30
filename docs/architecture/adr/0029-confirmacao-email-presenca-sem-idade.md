# ADR-0029 — Confirmação de e-mail e presença sem verificação etária

Status: decisão de produto aceita na issue #93 em 2026-10-19, com adendo de ativação em 2026-10-28 (abaixo); implementação sujeita à revisão humana antes de merge/deploy. Substitui os requisitos etários do ADR-0024 e suas referências operacionais, inclusive adendos do mesmo dia.

## Decisão

Participação exige conta Active, e-mail confirmado e selfie Passed; bloqueios, sanções e demais requisitos bilaterais continuam válidos. Idade declarada no perfil não é inferida nem verificada. VerifiedAdult, NotAdult e callbacks antigos não concedem nem retiram autorização. Contas Restricted/Suspended/DeletionPending não são liberadas automaticamente.

E-mail: código criptograficamente aleatório de oito dígitos, validade de 15 minutos desde emissão, hash com security stamp, senha obrigatória, consumo único sob lock. Cinco erros invalidam a prova; recadastro substitui senha/prova apenas quando admitido. Limite persistente de três emissões por conta/hora, além dos limites HTTP existentes. Links antigos só são aceitos em registros sem código. A outbox contém temporariamente o código para entrega; deve ser protegida e esvaziada conforme a retenção existente. SMS continua fora do escopo.

Selfie: adaptador AWS Rekognition Face Liveness e avaliação de qualidade via DetectFaces com Attributes DEFAULT exclusivamente. Sem estimativa de idade, comparação de identidade, documentos, upload alternativo ou aprovação pelo navegador. SDKs AWS .NET e Amplify têm licença Apache-2.0; versões travadas nos manifests. Liveness, brilho, nitidez e pose precisam de política calibrada explícita. Sem valores aprovados, integração desativada.

Captura com componente oficial, sessão criada no servidor, credenciais STS temporárias apenas para StartFaceLivenessSession, respostas no-store, vínculo ao titular e CSRF. O worker consulta o resultado autenticado, usa deadline, lease e fencing; aprovação revalida o estado da conta sob lock. A API complete só sinaliza conclusão. Nenhum score enviado pela SPA é aceito.

## Privacidade e ativação

AuditImagesLimit=0; sem OutputConfig/S3, frames apenas em memória durante avaliação. Referência de sessão e feedback local são apagados após 24h; resultado mínimo, política e datas acompanham a conta. O procedimento de exclusão de conta precisa incluir a nova tabela; a exclusão física não está implementada no produto atual. Não confundir expiração da sessão AWS com eliminação contratual.

Ativação requer revisão de DPA, região/transferência, opt-out de uso secundário AWS, retenção no fornecedor, consentimento, calibração e acessibilidade. PrivacyApproved é uma configuração explícita, não prova jurídica. A CSP vigente bloqueia recursos externos/WASM do SDK; homologar assets e permissões restritas antes de habilitar, sem relaxamento global para contornar o problema. Alternativa assistida, testes com câmera real e VoiceOver/teclado continuam gates do piloto. Nenhum serviço foi contratado ou ativado.

## Migração e operação

Expand: nova identity.selfie_verifications, sem apagar evidências antigas. Coordenar web/worker: parar o consumidor etário antes de migrar; a migração encerra entregas etárias pendentes e invalida leases. Rotas antigas e callbacks retornam 410 sem processar resultados. Avisar titulares para refazer a etapa de presença; VerifiedAdult não é convertido em Passed. Restrições antigas precisam de revisão humana, sem liberação em massa.

Rollback operacional é desabilitar captura e manter o leitor novo/410; nunca restaurar autorização etária. Não executar Down em dados reais. Código/tabelas/testes etários remanescentes são compatibilidade histórica, não dependência registrada nos hosts. A autorização usa ParticipationEligibility.

## Adendo — 28/10/2026: ativação em produção

Status: aceito. Decisões do dono em 28/10/2026, registradas na issue #93; substituem, nos pontos abaixo, os gates de
ativação desta ADR.

1. **Retenção:** nenhuma imagem é guardada. O resultado aprovado (`Passed`, com datas e versão da política) fica
   enquanto a conta existir e mantém a participação; nas palavras do dono, "a selfie fica guardada mesmo após 30 dias
   e a pessoa não perde participação". Tentativas não aprovadas (recaptura, inconclusiva, expirada) saem 30 dias após
   a conclusão, ou após a expiração se nunca concluíram. Referência de sessão e feedback continuam saindo em 24 h. A
   exclusão física da conta leva as tentativas junto (FK `ON DELETE CASCADE`, migração `SelfieResultFollowsAccount`).
   A limpeza roda no worker mesmo com a integração desligada.
2. **Fornecedor e região:** AWS Rekognition Face Liveness em us-east-1, com o DPA padrão da AWS. `PrivacyApproved=true`
   registra essa decisão; não é parecer jurídico.
3. **Limiares** (política `aws-docs-2026-10-28`, padrões versionados de `SelfieProviderOptions`, com testes de fronteira):
   liveness ≥ 80 (faixa alta, "80 or 90", do AWS AI Service Card do Face Liveness, indicada contra deepfake e vídeo
   gravado); |pitch| < 30° e |yaw| < 45° ("Recommendations for facial comparison input images"; o limite mais estreito
   de pitch vale nos dois sentidos); |roll| < 45°, brilho ≥ 30 e nitidez ≥ 20 são pisos do projeto, porque a AWS não
   publica número para eles. Mudar um limiar exige nova `PolicyVersion`.
4. **Testes em aparelho real dispensados** pelo dono. Os gates de câmera real, VoiceOver e dispositivos físicos desta
   ADR deixam de bloquear a ativação; a validação é a automatizada (câmera sintética do Chromium, sem AWS).

Implementação: `PrivacyApproved=true` no `compose.prod.yml` (web e worker) e `Enabled` vindo de `DUORA_SELFIE_ENABLED` no
`duora.env` (Ansible `duora_selfie_enabled: "true"`; `false` é o rollback para releases anteriores). Cada serviço tem o próprio
usuário IAM, com só as chamadas que faz: `duora-selfie-web` faz `sts:AssumeRole` em
`arn:aws:iam::394877252106:role/duora-selfie-capture` (que só permite `StartFaceLivenessSession`) para a credencial
temporária do navegador; `duora-selfie-worker` faz `CreateFaceLivenessSession`, `GetFaceLivenessSessionResults` e
`DetectFaces`, em us-east-1. As chaves chegam ao SDK só pelos arquivos de segredo `SelfieVerification__AwsAccessKeyId` e
`SelfieVerification__AwsSecretAccessKey` em `/run/secrets` de cada serviço (origem: `/etc/duora/secrets/selfie-web-aws-*`
e `selfie-worker-aws-*`, cada par montado só no seu serviço), lidos pelo `SecretsDirectory` e passados explicitamente aos
clientes STS e Rekognition; a cadeia padrão do SDK (ambiente, perfis, IMDS) não é consultada, e o boot falha se a
selfie estiver ligada sem os dois. A CSP não é relaxada globalmente: só o documento `/account/selfie` recebe `'wasm-unsafe-eval'`,
`cdn.liveness.rekognition.amazonaws.com`, `wss://streaming-rekognition.us-east-1.amazonaws.com` e `camera=(self)`
([deploy.md](../../operations/deploy.md#documento-da-selfie-issue-93)).

Continuam fora desta decisão: alternativa assistida, recuperação dedicada de host/PostgreSQL e opt-out de uso secundário
na AWS Organizations (passo operacional do dono).

## Adendo — 30/10/2026: limite de reprovações (issue #300)

Decisão do dono de 29/10/2026, detalhada no [ADR-0043](0043-limite-de-reprovacoes-da-selfie.md): cinco reprovações
pausam a selfie por 1 hora; mais cinco, depois da pausa, suspendem a conta. As reprovações ficam em
`identity.selfie_failures`, à parte da retenção de 30 dias das tentativas, e saem na aprovação, na revogação da
suspensão por selfie e com a conta. A sessão da AWS encerrada como `EXPIRED` ou `FAILED` termina `Expired` e não conta.

## Evidências

[Testes e plano](../../superpowers/plans/2026-10-19-consolidacao-93.md). [Issue #93](https://github.com/BipoXDXD/duora/issues/93). As limitações de validação devem constar no relatório da entrega; mocks do fornecedor não equivalem a homologação AWS.
