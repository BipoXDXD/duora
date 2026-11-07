# Proposta de aviso de privacidade — reprovações da selfie (#304)

**Estado: minuta para revisão, não aprovada e não publicada.** Preparada em 31/10/2026. Os textos completos
[PT-BR](pt-BR.md) e [EN-US](en-US.md) substituem, como proposta, o aviso inteiro; mantêm os parágrafos existentes
fora da seção de selfie e acrescentam limites e revisão humana. A identidade `draft-selfie-failures-304` é
provisória, não uma versão legal escolhida para publicação. Ambos têm `approved: false`.

## Escopo e evidência

Comparação feita contra a `main` no commit
[`b4bdee1c7b2a3a8bf023d1fafcf346610dd5e13b`](https://github.com/BipoXDXD/duora/tree/b4bdee1c7b2a3a8bf023d1fafcf346610dd5e13b).
O [PR #304](https://github.com/BipoXDXD/duora/pull/304) foi integrado em 30/10/2026 às 05:13:18 UTC
(02:13:18 em America/Sao_Paulo), pelo merge `678c589b3197f13592f960c4c152822a4b26d627`.
A [confirmação do dono](https://github.com/BipoXDXD/duora/pull/304#issuecomment-6514065721) aceita os defaults
e mantém a exigência de publicar o aviso antes da ativação. Ela não é aprovação destes novos textos.

Objetivo: transparência sobre dados mínimos, retenção e decisão automatizada já implementados. Invariantes
relacionadas: INV-01 (elegibilidade), INV-06 (idempotência), INV-10 (rastreabilidade/revisão) e INV-12
(limites de operação). Contratos relacionados: API-P05/P06, API-A08, API-ADM21–23 e futuro API-S06;
aceites T-29 e parte implementada de T-10. Não há alteração de comportamento, autorização, contrato HTTP,
SQL, migração, configuração, concorrência ou telemetria nesta proposta.

O merge e as configurações versionadas não comprovam o estado da produção. Nenhum ambiente de produção,
credencial ou dado de participante foi consultado. Esta revisão não publica documentos, não habilita captura,
não executa suspensão, exclusão ou recurso e não constitui parecer de conformidade jurídica.

## Comparação com a implementação

| Tema | Código e política efetiva na revisão | Tratamento nas duas minutas |
|---|---|---|
| Tentativa original | `SelfieVerificationDispatcher.PurgeExpiredDataAsync`: elimina `identity.selfie_verifications` não `Passed` quando `completed_at` tem pelo menos 30 dias; sem conclusão, usa `expires_at`. O expurgo roda mesmo com a captura desabilitada. Referência de sessão e feedback são limpos após 24 h da expiração. | Explica o marco inicial dos 30 dias e a rotina de limpeza, sem prometer eliminação instantânea no fornecedor. |
| Resultado aprovado | `Passed`, política e datas acompanham a conta; não há nova selfie por simples decurso de 30 dias. | Conserva a informação sobre resultado aprovado enquanto a conta existir. |
| Registro mínimo separado | `identity.selfie_failures`: `verification_id` (PK), `user_id` (FK) e `failed_at`; sem imagem, score, feedback ou motivo do fornecedor. Sem FK para a tentativa. `ON CONFLICT DO NOTHING` impede contar a mesma tentativa duas vezes. | Explica os três dados e a finalidade de manter o limite mesmo depois do expurgo das tentativas originais. Não chama esse registro de anônimo. |
| Duração da contagem | Sem expiração por idade. `SelfieFailureLedger.ClearAsync` é chamado na aprovação e na revogação da suspensão por selfie. A FK remove o registro na exclusão física da conta. Revogar outra suspensão não limpa a contagem. | Distingue expressamente da retenção de 30 dias e inclui todos os eventos de remoção. Não inventa teto de retenção. |
| O que conta | `SelfieAttemptPolicy.CountsAsFailure`: só `RecaptureRequired` ou `Inconclusive` final, na mesma `PolicyVersion` da tentativa. Expiração, indisponibilidade, falha técnica e divergência de política não contam. O adaptador trata `EXPIRED`, `FAILED` e resultados sem dados utilizáveis como `Expired`. | Usa “reprovação contabilizada”; não usa a expressão ampla “qualquer tentativa não aprovada” da proposta original do PR. |
| Pausa | Cinco reprovações; reabertura exatamente em `failed_at` da quinta + 1 h. Depois, até cinco novas reprovações. Os limites de cinco pedidos/h e um minuto continuam. | Informa origem da hora, dois ciclos e ausência de reset por espera. |
| Esgotamento e suspensão | Na décima reprovação, a transação grava pedido na outbox. `SelfieAttemptsSuspensionDispatcher` revalida a contagem e chama a suspensão fora da transação, com chave idempotente. Outra suspensão faz o pedido esperar; falhas podem exigir revisão operacional. A recusa de novas tentativas independe de o consumidor já ter suspendido. | Distingue pedido automático de suspensão efetivada; não promete suspensão síncrona ou prazo de processamento. Explica a espera por outra medida. |
| Efeito e revisão | `SelfieVerificationFailed`, autor `Automated`, sem expiração; só moderador revoga. A participação é negada durante a suspensão. A revogação limpa a contagem, mas não aprova a selfie. | Declara decisão sem revisão humana prévia, efeito sobre participação, ausência de prazo e consequência de uma revogação. Pedido de revisão não significa liberação automática. |
| Canal disponível | ADR-0043 prevê pedido ao suporte e revogação pelo moderador. API-S06 ainda não existe; a tela informa que a revisão não está disponível pelo app. `support-contact.md` registra canal PT-BR/EN-US com evidência histórica de recebimento. | Informa `support@meetduora.com`, utilizável sem sessão, e a ausência do recurso no aplicativo. Não promete outro revisor, prazo ou alternativa assistida inexistentes. |

Fontes de implementação: [política de tentativas](../../../../backend/src/Duora.Core/Identity/SelfieAttemptPolicy.cs),
[ledger](../../../../backend/src/Duora.Infrastructure/Identity/SelfieFailureLedger.cs),
[migração](../../../../backend/src/Duora.Infrastructure/Persistence/Migrations/20261030120000_SelfieFailures.cs),
[resultado e expurgo](../../../../backend/src/Duora.Infrastructure/Identity/SelfieVerificationDispatcher.cs),
[adaptador AWS](../../../../backend/src/Duora.Infrastructure/Identity/RekognitionSelfieProvider.cs),
[consumidor da suspensão](../../../../backend/src/Duora.Infrastructure/Identity/SelfieAttemptsSuspensionDispatcher.cs),
[suspensão e revogação](../../../../backend/src/Duora.Infrastructure/Safety/AccountSuspensions.cs).
Política e contratos: [retenção](../../retention.md), [mapa de dados](../../data-map.md),
[ADR-0029](../../../architecture/adr/0029-confirmacao-email-presenca-sem-idade.md),
[ADR-0042](../../../architecture/adr/0042-suspensao-de-conta-por-violacao-dos-termos.md),
[ADR-0043](../../../architecture/adr/0043-limite-de-reprovacoes-da-selfie.md),
[T-29/T-10](../../../testing/acceptance.md), [suporte](../../../operations/support-contact.md).

## Consistência com documentos e contratos

Os avisos publicados de 25/10/2026, em [PT-BR](../../../legal/privacy/2026-10-25/pt-BR.md) e
[EN-US](../../../legal/privacy/2026-10-25/en-US.md), e seus sucessores de
[07/11/2026](../../../legal/privacy/2026-11-07/pt-BR.md), não distinguem os dois conjuntos de registros e não
explicam a suspensão automatizada. Eles ficam intactos para preservar a identidade do conteúdo já aceito.
As minutas acrescentam os mesmos fatos nos mesmos dez títulos de seção correspondentes. Preservam controlador,
contato, fornecedores e demais parágrafos existentes, sem aproveitar a revisão para mudar outras finalidades.

Os Termos ativos de 07/11/2026, em [PT-BR](../../../legal/terms/2026-11-07/pt-BR.md) e
[EN-US](../../../legal/terms/2026-11-07/en-US.md), remetem moderação e recursos a fluxos próprios e distinguem
automação de julgamento humano. Não foi encontrada incompatibilidade textual com a explicação proposta.
Não se altera a declaração de maioridade nem se apresenta presença como verificação de idade ou identidade.
Necessidade jurídica de atualizar também os Termos continua sendo decisão do controlador, sem reescrita silenciosa.

Os DTOs de [selfie](../../../../backend/src/Duora.Contracts/Identity/SelfieContracts.cs) expõem apenas estado,
restante do ciclo e reabertura, não um histórico de falhas ou score. Os de
[suspensão](../../../../backend/src/Duora.Contracts/Safety/AccountSuspensionContracts.cs) distinguem decisão
automática e revogação por moderador; os [documentos públicos](../../../../backend/src/Duora.Contracts/Identity/LegalDocumentContracts.cs)
preservam versão, idioma, publicação e digest. Nada exige ampliar DTOs, criar um endpoint de recurso ou expor
evidência interna para publicar futuramente o texto proposto.

Fornecedor: a região e o DPA padrão da AWS são os já registrados no ADR-0029 e no aviso atual. “O Duora não
guarda imagens” descreve o armazenamento do Duora, não uma garantia de eliminação na AWS. A mudança não
declara opt-out, retenção contratual do fornecedor ou transferência internacional regularizados; a documentação
de dados mantém essas avaliações separadas. Não foi feita nova análise jurídica dos contratos de fornecedores.

## Publicação futura: aprovação, versão e fingerprints

O procedimento vigente está em [public-legal-documents.md](../../../identity/public-legal-documents.md).
Esta entrega mantém as minutas fora de `docs/legal` e dos dois catálogos. Apenas marcar `approved: false`
dentro do catálogo não bastaria para mantê-las privadas: o endpoint devolve todos os documentos carregados,
inclusive os não publicados. Selecionar uma versão ativa não aprovada também fecha o cadastro. Nenhuma dessas
mudanças é feita aqui.

Quando houver autorização própria para publicar:

1. O controlador deve aprovar explicitamente os textos finais nos dois idiomas e a versão/data legal final.
   Registrar a decisão e o conteúdo exato aprovado; a confirmação da regra de produto no PR #304 não supre isso.
2. Copiar os textos para `docs/legal/privacy/<nova-versao>/pt-BR.md` e `en-US.md`, substituir a versão provisória
   nos metadados e usar `approved: true` somente após essa aprovação. Não editar os arquivos de 25/10/2026.
3. Atualizar `privacyVersion` e incluir os dois caminhos em `docs/legal/catalog.json`, mantendo os documentos
   anteriores para resolução exata dos aceites. Os Termos ativos permanecem em 07/11/2026 salvo decisão própria.
4. Regenerar o catálogo incorporado com `python3 scripts/supply-chain/legal_documents.py --write`. Obter os novos
   digests pela implementação de `PublicLegalDocument.Sha256`: SHA-256 do JSON compacto UTF-8 com serialização
   web do .NET, na ordem `document`, `version`, `locale`, `title`, `controllerName`, `privacyContact`, `sections`
   (cada seção: `title`, `paragraphs`). Não usar o hash do Markdown nem uma serialização Python aproximada.
   `approved` não participa do digest, mas a versão e cada tradução participam: os hashes da minuta não seriam
   os da publicação com outra versão.
5. Adicionar os novos fingerprints ao teste `PublishedDocumentKeepsItsExactFingerprint`, preservando todos os
   casos das versões publicadas. Rodar os testes de publicação, recibos exatos, retenção de versões e o pipeline
   obrigatório, sem reduzir gates. Novos cadastros devem aceitar os documentos ativos exatos. Não inferir,
   regravar nem preencher retroativamente idioma, versão ou hash em aceites históricos.
6. Registrar publicação e validação da nova versão antes da autorização separada de ativação. Uma atualização
   do aviso não cria consentimento ou reaceite de privacidade para contas antigas: o fluxo de reaceite existente
   é dos Termos. O controlador deve decidir como informar titulares existentes e se outro mecanismo é necessário.

Os seis checks obrigatórios (`backend`, `frontend`, `e2e`, `secret-scan`, `contract`, `supply-chain`), os
fingerprints antigos, o gate fail-closed de cadastro e a revisão humana permanecem. A autorização desta tarefa
termina na preparação para revisão; não inclui executar os passos de publicação ou ativação acima.

## Lacunas para a revisão

- O ADR-0043 ainda está `Proposed` e menciona defaults pendentes, embora o comentário do dono os confirme.
  Esta proposta registra a evidência, mas não promove o status arquitetural nem o confunde com aprovação legal.
- O recurso API-S06 não está implementado. O suporte e a revogação pelo moderador são o caminho documentado;
  capacidade atual de atendimento e eventual revisão independente precisam de avaliação própria. A tela de conta
  suspensa hoje não oferece link de suporte; isso é uma melhoria de interface separada, não um recurso já existente.
- A contagem pode durar indefinidamente sem aprovação/revogação. Finalidade, necessidade, base legal e
  proporcionalidade dessa conservação e da suspensão sem prazo exigem revisão do controlador; a minuta descreve
  a implementação e não inventa expiração que o código não aplica.
- A exclusão física da conta remove os registros por FK, mas o fluxo de exclusão física ainda é apontado como
  ausente no mapa de dados. O texto mantém o canal de solicitação, sem prometer autoatendimento ou prazo novo.
- A exceção `FAILED` é uma classificação operacional do projeto; não se apresenta como conclusão da AWS sobre
  todas as causas possíveis. Calibração, fornecedor, acessibilidade e operacionalização do suporte mantêm seus gates.
- Integração, aprovação legal, publicação e ativação são estados distintos. Desligar captura não encerra a
  retenção nem o consumidor de suspensões já enfileiradas; não é um rollback completo dessas obrigações.

## Validação desta proposta

Comandos executados localmente:

- `python3 scripts/supply-chain/legal_documents.py` — passou: fontes canônicas iguais ao catálogo incorporado.
- `python3 -m unittest discover -s scripts/supply-chain -p test_legal_documents.py -v` — seis testes, seis aprovações.
- `git diff --cached --check` — passou, sem erro de whitespace.

Uma verificação Python adicional com o parser real `parse_document` confirmou: duas minutas válidas com
`approved: false`, identidade provisória, dez seções e a mesma distribuição de parágrafos; parágrafos anteriores
não relacionados à selfie preservados; minutas ausentes do catálogo; versões ativas inalteradas; links relativos
válidos. A comparação com `HEAD` confirmou que fontes publicadas, catálogos, testes de fingerprint, contratos,
runtime, scripts, infraestrutura, workflows, `AGENTS.md` e `docs/automation` não foram modificados.

Arquivos alterados: este relatório, `pt-BR.md`, `en-US.md` e links para a proposta em
`docs/identity/public-legal-documents.md` e no ADR-0043. As alterações ficam na branch
`docs/selfie-privacy-proposal-304`, destinadas a um PR em rascunho para revisão. O envio dessa branch não
publica uma versão legal nem autoriza a integração à `main` ou a ativação da funcionalidade.

Não se executam build .NET, PostgreSQL, testes de comportamento da selfie, OpenAPI gerado ou navegador nesta
revisão documental. Além de não haver alteração executável, o procedimento local de documentos legais reserva
compilação .NET, integração e browser real aos gates hospedados. Não se atribuem a esta entrega os resultados
de testes históricos do PR #304. Uma futura publicação continua sujeita à CI completa e revisão humana.
Não foram detectadas tentativas de instrução não confiável nos materiais consultados.
