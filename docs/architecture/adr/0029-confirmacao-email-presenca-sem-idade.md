# ADR-0029 — Confirmação de e-mail e presença sem verificação etária

Status: decisão de produto aceita na issue #93 em 2026-10-19; implementação sujeita à revisão humana antes de merge/deploy. Substitui os requisitos etários do ADR-0024 e suas referências operacionais, inclusive adendos do mesmo dia.

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

## Evidências

[Testes e plano](../../superpowers/plans/2026-10-19-consolidacao-93.md). [Issue #93](https://github.com/BipoXDXD/duora/issues/93). As limitações de validação devem constar no relatório da entrega; mocks do fornecedor não equivalem a homologação AWS.
