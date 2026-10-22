# ADR-0035: Revisão humana de fotos antes da publicação

- **Status:** Proposed
- **Origem:** [issue #173](https://github.com/BipoXDXD/duora/issues/173), [#69](https://github.com/BipoXDXD/duora/issues/69), [ADR-0025](0025-pipeline-de-fotos-imagesharp-s3-outbox.md), spec 12.3 e API-M01–M04.
- **Revisar quando:** Estados/contrato, regras editoriais, retenção, equipe ou fronteira Media/Safety mudarem.

Formalização da investigação de 21/10/2026, baseada na main `c6af25d53f9e5844e6ae92755ea3466504a673cb`.
As recomendações abaixo são candidatas, não decisões aceitas. Os responsáveis por função ainda não foram
nomeados e os itens P173-* permanecem pendentes. Aprovar ou integrar este PR documental não aprova o desenho,
não autoriza implementação e não satisfaz os gates de ativação.

## Relação com decisões vigentes

Propõe detalhar a futura revisão humana e publicação de candidatos privados, prevista como pendência no ADR-0025.
O ADR-0025 permanece Accepted e inalterado: este documento não o substitui enquanto Proposed. No aceite
futuro, identificar precisamente as partes substituídas e registrar referências recíprocas conforme as regras
do [índice](README.md#regras). A proposta complementar está no [ADR-0036](0036-isolamento-do-media-sanitizer.md).
O ADR-0034 e o contrato legado/opt-in já implementados permanecem vigentes; a política humana e o sandbox
não são uma continuação da migração de validação.

## Contexto e evidências

- [UploadLifecycle](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Core/Media/UploadLifecycle.cs) tem seis estados e nenhuma espera editorial. As transições de `Processing` só admitem retomada, aprovação, rejeição ou exclusão. [MediaModelConfiguration](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Infrastructure/Media/MediaModelConfiguration.cs) restringe estados/códigos por CHECK e mantém índices parciais.
- [PublishAsync](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Infrastructure/Media/MediaProcessingDispatcher.cs#L318) consulta o veredito, mas depois chama **sempre** `MediaAsset.Approve`. Acrescentar apenas um enum/política não cria revisão segura: deixaria a criação do asset aprovado incompatível com a espera humana.
- [PhotoUploads.AwaitingVerdict](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Infrastructure/Media/PhotoUploads.cs#L183) usa uma expressão própria para as cotas por conta e global. Alterar só `UploadLifecycle.PendingStates` não atualiza essa contagem. Defaults atuais: 3 por conta e 100 globais; não são alterados por esta proposta.
- O prazo de 24 h de [MediaProcessingOutbox](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Infrastructure/Media/MediaProcessingOutbox.cs) é de processamento/abandono técnico. Não existe SLA, TTL nem fila editorial implementados. Não segurar lease/outbox de sanitização enquanto uma pessoa revisa.
- [PhotoEndpoints](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Web/Media/PhotoEndpoints.cs#L223) expõe `State.ToString()` no status; o replay do upload também devolve o estado atual. Status ser `string` no OpenAPI/tipo gerado **não prova compatibilidade semântica** de um valor novo. API-M03/M04 estão na spec, mas não estão mapeadas nesse fluxo; a retenção operacional examinada não implementa limpeza editorial de mídia. Não pressupor exclusão/leitura privada/limpeza prontas.
- [AdminEndpoints](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Web/Admin/AdminEndpoints.cs) já exige sessão administrativa/MFA; [AccountRoles](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Core/Identity/AccountRoles.cs) separa contas privilegiadas de participantes. Isso não concede permissão para qualquer caso. O módulo Safety tem conceitos de domínio de denúncia/medida/recurso, mas sua persistência atual só mapeia guardas/bloqueios: não há painel/fila de revisão de fotos pronto para reutilizar.
- [AuditTrail](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Infrastructure/Audit/AuditTrail.cs) exige a transação do efeito e grava um catálogo fechado de ações, ator/alvo/versão. Política, motivo e evidência precisam de registro estruturado próprio, com referência de auditoria; não cabem como texto sensível em logs.
- [ObjectKey/StorageArea](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Core/Media/ObjectKey.cs), FileSystemObjectStore e o deploy reconhecem só `quarantine`/`derivatives`. O [backup](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/scripts/deploy/duora_backup.py#L116) copia `derivatives`, não faz SELECT de assets Approved. Colocar candidatos nessa área incluiria material ainda não aprovado nas cópias e exigiria redecidir retenção/restore.

## Alternativas

| Opção | Benefício | Custo/risco | Parecer proposto |
|---|---|---|---|
| Adicionar política e retornar outro veredito no fluxo atual | Mudança pequena | Factory ainda aprova; falta caso, autorização, auditoria e retenção | Descartar |
| Manter `Processing` e uma fila editorial lateral | Pode evitar valor novo na resposta | Mistura significado técnico/humano, complica retry/expiração e pode esconder estado ao cliente | Possível apenas com projeção e semântica explicitamente aprovadas; não é compatibilidade automática |
| Estado explícito `AwaitingReview`, candidato não público e caso editorial próprio | Fronteiras e expiração explícitas; não confunde falha técnica com decisão humana | Exige evolução de schema, contrato e armazenamento, mais fluxo administrativo | **Recomendação para revisão** |

## Decisão proposta, sujeita às decisões pendentes

1. Preservar `TechnicalOnly` exclusivamente para Development/Testing. Nome provisório da nova política: `HumanReview` (não é enum disponível). O nome, os ambientes permitidos e o comportamento de Staging só entram após aceite explícito; nenhum ambiente desconhecido herda permissão.
2. Após sanitização bem-sucedida, persistir candidato/variantes em área privada de revisão, sem criar asset aprovado. Em uma transação curta, registrar a geração/hash do candidato, mover `Processing → AwaitingReview` e criar o caso editorial durável. Encerrar a entrega técnica após esse commit; apagar o original após a derivação/commit com limpeza durável para falhas, sem alongar a retenção do original para esperar um moderador.
3. Propor caso/decisão sob responsabilidade de Safety, e estado de upload/asset/arquivo sob Media; aplicar a decisão por serviço de aplicação com a mesma unidade transacional já permitida pelo monólito. Não inventar uma denúncia de usuário só para encaixar um upload na tabela de reports; não depender de implementar todo o produto de moderação.
4. Lista paginada e evidência somente de casos atribuídos/autorizados. Propor `moderator` com permissão específica, sessão administrativa e MFA; `product_admin`/`operator` não ganham acesso a fotos por serem privilegiados. Decisão exige step-up e versão esperada; leitura de evidência é auditada **antes** de transmitir bytes, falhando fechada se a auditoria falhar. Não criar thumbnails na lista por padrão; interface e mensagens de uso em PT-BR/EN-US.
5. Aprovação prepara a cópia dos derivados para a área final fora de transação longa. O commit condicional pela versão faz upload/asset Approved, decisão, auditoria e eventual efeito de outbox necessários de forma atômica. Arquivo copiado sem esse commit nunca se torna público. Chaves/gerações e limpeza devem impedir que uma tentativa antiga sobrescreva ou apague a vencedora.
6. Reprovação, expiração e exclusão nunca aprovam nem sancionam a conta implicitamente. Repetição idempotente devolve a decisão já registrada; decisão divergente ou versão antiga é conflito. Exclusão/restrição/revogação de permissão concorrentes são revalidadas sob as guardas/versões pertinentes. Recurso cria nova revisão autorizada, preservando o histórico; não implementa `Rejected → Approved` cego.
7. Incluir a espera editorial nas cotas atuais por conta/global e respectivas consultas/índices; não abrir fila infinita nem aumentar 3/100 para compensar falta de revisores. Definir TTL e alertas próprios, separados do lease e das 24 h técnicas.
8. Preferir área de candidatos separada de derivados finais e fora do backup de mídia corrente. Isso **requer** proposta de StorageArea/chaves, permissões, marcadores/montagens e política de backup/restore; não é uma área já disponível. Moderador acessa derivado sanitizado por rota administrativa autorizada, nunca original ou caminho interno. Restore com candidato ausente mantém o caso não publicável para nova coleta/expiração conforme política aprovada, jamais promove a Approved. A ausência dos bytes candidatos no backup de mídia não elimina os metadados/casos presentes nas cópias do banco; retenção e reconciliação desses registros também precisam ser aprovadas.

## Decisões pendentes e responsáveis por função

| ID | Decisão a revisar | Recomendação / consequência | Aceite necessário |
|---|---|---|---|
| P173-1 | Nome/ambientes e regras editoriais | Política humana explícita; nenhum fallback técnico em produção | Produto + responsável técnico |
| P173-2 | Estado e contrato público | Preferir `AwaitingReview`; avaliar também projeção para contrato antigo. Atualizar enum/checks, DTO/docs, replay e consumidores antes de escrever novos estados | Engenharia/API + responsável técnico |
| P173-3 | Rejeição/expiração editorial | Códigos estáveis mínimos no status, separados dos motivos internos. CHECK atual só conhece rejeições técnicas; não usar `PROCESSING_LIMIT` para disfarçar decisão editorial | Produto/privacidade + API |
| P173-4 | Dono do caso e permissões | Safety decide/audita; Media publica; permissão por atribuição, step-up e revisão independente de recurso | Segurança + produto |
| P173-5 | TTL, retenção, recurso e ausência de revisão | Prazo explícito para candidato/evidência e comportamento de fila sem responsável; sem retenção infinita nem aprovação automática | Produto/privacidade + operação |
| P173-6 | Área candidata e backups | Preferir área privada separada; definir destino após restore e limpeza de órfãos/tombstones | Engenharia/storage + operação |
| P173-7 | Atendimento real | Nomear responsável e substituto, fluxo de atribuição e escalonamento; não pressupor equipe disponível | Responsável pelo produto |

As funções acima não são pessoas já designadas. Esta investigação registra escolhas abertas, sem pedir nem presumir seu aceite.

## Entregas posteriores e dependências

- Antes de implementar: resolver e registrar as decisões pendentes, obter os aceites indicados e atualizar este ADR para Accepted por revisão explícita. A publicação deste documento como Proposed não satisfaz esse gate.
- Entrega de persistência/compatibilidade: expandir schema e leitores **antes** de escritores da nova política; casos, constraints, índices, cotas e projeção da API testados. O opt-in de validação #154 trata outro campo/negociação e não resolve evolução do status.
- Entrega do caso humano: operações administrativas de atribuição/leitura/decisão/recurso, auditoria e teste de concorrência; sem tornar SQL manual ou acesso ao storage a interface de revisão.
- Entrega de armazenamento/retenção/exclusão: candidato privado, promoção, coleta de órfãos e exclusão lógica/física. Delimitar trabalho de API-M03/M04 e T-26 ausente antes de considerar pronta a experiência com fotos reais; escopo/issue própria ainda a definir, não prometido por este estudo.
- #174 pode avançar em paralelo mantendo `IPhotoSanitizer` como porta de sanitização, sem política ou decisão editorial no sandbox. A ativação da #69 depende das duas entregas, de #86 e da homologação B2/backup/restore/montagens/verify já registrada, além de autorização própria de release.
- Rollback: interruptor vazio impede novos uploads, mas **não** torna um binário antigo capaz de ler `AwaitingReview` no banco. Exigir versão de rollback com leitores compatíveis, migração aditiva e teste real de retorno. Não reclassificar pendências como Approved nem apagar histórico para caber no binário anterior.

## Compliance e evidência futura

Cobrir T-01/03/05/06/08/10/13/16/26 relevantes e INV-03/05/06/08/10/11: HTTP positivo/negativo para cada permissão; PostgreSQL real com decisões concorrentes/repetidas e exclusão concorrente; falha entre gravação/cópia/commit; caso pendente sem arquivo após restore; auditoria indisponível sem transmissão; cotas preservadas e limpeza; falha de revisão sem publicação; novo status no replay/cliente; contrato e rollback. Testes futuros no CI com dados sintéticos e nove checks aprovados no SHA final, mais evidência de homologação apropriada.

## Consequências

- Positivas: separa sanitização de aprovação humana e torna a decisão rastreável e revisável.
- Negativas: exige fila, operação humana, storage privado, retenção e expansão compatível de persistência/contrato.
- Trade-off: espera humana consome cotas e disponibilidade; não pode ser resolvida por aprovação automática.

## Segurança e operação

Os controles e procedimentos descritos acima são requisitos para trabalho futuro, não comprovação de sua
implementação. Manter `duora_media_review: ""`, `duora_media_backup_enabled: false` e a allowlist de Production
vazia. Não liberar TechnicalOnly em Production/Staging, nem alterar limites, políticas executáveis, API,
segredos, infraestrutura ou ativação neste PR. Dados reais, deploy e homologação requerem autorização própria.
A #69 continua bloqueada pelas duas entregas, pela #86 e pelos gates B2/backup/restore/montagens/verify.

A futura implementação deve produzir a evidência de testes e homologação descrita acima, com dados
sintéticos, revisão humana e nove checks obrigatórios no SHA final. O CI deste PR valida a alteração
documental contra as suítes existentes; não comprova uma política humana ou sandbox ainda inexistentes.
Não executar builds, testes, Docker ou VM no Mac do responsável. Resultados do CI pertencem ao PR; nenhum
resultado futuro é presumido neste ADR. Issues #173, #174 e #69 permanecem abertas.
