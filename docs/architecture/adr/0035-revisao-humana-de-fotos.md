# ADR-0035: Revisão humana de fotos antes da publicação

- **Status:** Accepted (2026-10-22); emendado pelas [decisões de 28/10/2026](#decisões-de-28102026) (HumanReview admitida em Production, sem recurso de fotos, revisor único) e pelas [decisões de 29/10/2026](#decisões-de-29102026) (espera de 7 dias, retenção de 1 ano dos registros de exclusão); ativação do upload pendente (#69)
- **Origem:** [issue #173](https://github.com/BipoXDXD/duora/issues/173), [#69](https://github.com/BipoXDXD/duora/issues/69), [ADR-0025](0025-pipeline-de-fotos-imagesharp-s3-outbox.md), spec 12.3 e API-M01–M04.
- **Revisar quando:** Estados/contrato, regras editoriais, retenção, equipe ou fronteira Media/Safety mudarem.

## Registro do aceite — 22/10/2026

O responsável aceitou nesta conversa as escolhas concretas apresentadas após o PR #175:
“Aceito as escolhas, com P174-3/4 como candidatos de homologação e nomes da P173-7 pendentes.”
Autorizou somente registrar o aceite em PR documental e validar pelo CI. Não autorizou implementação,
ativação, deploy nem builds, testes ou Docker no Mac. Os identificadores P173/P174 são preservados para
rastrear decisões aceitas e condições remanescentes; não representam todas decisões ainda abertas.

A investigação original de 21/10/2026 usou a main `c6af25d53f9e5844e6ae92755ea3466504a673cb`.
As evidências abaixo descrevem esse baseline, não funcionalidades entregues por este aceite.

## Relação com decisões vigentes

Este ADR substitui parcialmente, como arquitetura de destino, o item 8 (revisão) e evolução do estado/resultado público do item 10 do
[ADR-0025](0025-pipeline-de-fotos-imagesharp-s3-outbox.md). O ADR-0025 permanece Accepted nas demais partes,
com adendo recíproco; seu relato da implementação existente permanece histórico.
A decisão complementar está no [ADR-0036](0036-isolamento-do-media-sanitizer.md).
O ADR-0034 e o contrato legado/opt-in permanecem vigentes. Este PR não altera o contrato executável,
a política efetiva, limites ou configuração. Aceite arquitetural não comprova implementação ou homologação.

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

| Opção | Benefício | Custo/risco | Resultado da decisão |
|---|---|---|---|
| Adicionar política e retornar outro veredito no fluxo atual | Mudança pequena | Factory ainda aprova; falta caso, autorização, auditoria e retenção | Descartar |
| Manter `Processing` e uma fila editorial lateral | Pode evitar valor novo na resposta | Mistura significado técnico/humano, complica retry/expiração e pode esconder estado ao cliente | Não escolhida; decisão aceita usa estado explícito |
| Estado explícito `AwaitingReview`, candidato não público e caso editorial próprio | Fronteiras e expiração explícitas; não confunde falha técnica com decisão humana | Exige evolução de schema, contrato e armazenamento, mais fluxo administrativo | Escolhida |

## Decisão aceita — implementação futura

1. Preservar `TechnicalOnly` exclusivamente para Development/Testing. Nova política de destino: `HumanReview` (ainda não é enum disponível), nos quatro ambientes conhecidos; Staging somente com dados sintéticos. Nenhum ambiente desconhecido herda permissão. *(Ambientes substituídos pelo adendo de 28/10/2026: HumanReview em Development, Testing e Production; Staging ainda não decidido e recusado pelo código.)*
2. Após sanitização bem-sucedida, persistir candidato/variantes em área privada de revisão, sem criar asset aprovado. Em uma transação curta, registrar a geração/hash do candidato, mover `Processing → AwaitingReview` e criar o caso editorial durável. Encerrar a entrega técnica após esse commit; apagar o original após a derivação/commit com limpeza durável para falhas, sem alongar a retenção do original para esperar um moderador.
3. Manter caso/decisão sob responsabilidade de Safety, e estado de upload/asset/arquivo sob Media; aplicar a decisão por serviço de aplicação com a mesma unidade transacional já permitida pelo monólito. Não inventar uma denúncia de usuário só para encaixar um upload na tabela de reports; não depender de implementar todo o produto de moderação.
4. Lista paginada e evidência somente de casos atribuídos/autorizados. Exigir `moderator` com permissão específica, sessão administrativa e MFA; `product_admin`/`operator` não ganham acesso a fotos por serem privilegiados. Decisão exige step-up e versão esperada; leitura de evidência é auditada **antes** de transmitir bytes, falhando fechada se a auditoria falhar. Não criar thumbnails na lista por padrão; interface e mensagens de uso em PT-BR/EN-US.
5. Aprovação prepara a cópia dos derivados para a área final fora de transação longa. O commit condicional pela versão faz upload/asset Approved, decisão, auditoria e eventual efeito de outbox necessários de forma atômica. Arquivo copiado sem esse commit nunca se torna público. Chaves/gerações e limpeza devem impedir que uma tentativa antiga sobrescreva ou apague a vencedora.
6. Reprovação, expiração e exclusão nunca aprovam nem sancionam a conta implicitamente. Repetição idempotente devolve a decisão já registrada; decisão divergente ou versão antiga é conflito. Exclusão/restrição/revogação de permissão concorrentes são revalidadas sob as guardas/versões pertinentes. ~~Recurso cria nova revisão autorizada, preservando o histórico; não implementa `Rejected → Approved` cego.~~ *(Substituído pelo adendo de 28/10/2026: não há recurso; a foto recusada continua `Rejected` e a pessoa envia uma foto nova, que abre revisão nova. Nada faz `Rejected → Approved`.)*
7. Incluir a espera editorial nas cotas atuais por conta/global e respectivas consultas/índices; não abrir fila infinita nem aumentar 3/100 para compensar falta de revisores. Aplicar os prazos de P173-5 e detalhar alertas próprios, separados do lease e das 24 h técnicas.
8. Criar área `review-candidates` separada de derivados finais e fora do backup de mídia corrente. Isso **requer** proposta de StorageArea/chaves, permissões, marcadores/montagens e política de backup/restore; não é uma área já disponível. Moderador acessa derivado sanitizado por rota administrativa autorizada, nunca original ou caminho interno. Restore com candidato ausente encerra a tentativa com `PHOTO_REVIEW_UNAVAILABLE` e orienta novo envio, jamais promove a Approved. A ausência dos bytes candidatos no backup de mídia não elimina os metadados/casos presentes nas cópias do banco; a execução da retenção de P173-5 e a reconciliação desses registros precisam ser detalhadas e verificadas.

## Escolhas aceitas e condições remanescentes

| ID | Escolha aceita | Dependência ou risco remanescente |
|---|---|---|
| P173-1 | `HumanReview` em Development, Testing e Production (adendo de 28/10/2026); Staging ainda não decidido, pois o código o recusa. Foto apresentada como do titular, sem terceiros identificáveis, nudez/conteúdo sexual explícito, violência gráfica, ódio ou dados de contato expostos. Dúvida nunca leva a aprovação automática; a segunda análise por outra pessoa foi substituída pelo adendo de 28/10/2026, que não prevê segundo revisor. Não realizar reconhecimento facial nem inferir idade ou atributos pessoais. | Regras e orientação do revisor em PT-BR/EN-US. Revisão visual não comprova identidade, consentimento ou idade; risco de inconsistência humana. |
| P173-2 | `AwaitingReview` persistido e público, inclusive replay; não projetar como `Processing`. Preparar leitores, constraints, índices, cotas e consumidores antes dos escritores. | Evolução semântica exige revisão própria de contrato e testes. Rollback só com leitores compatíveis. |
| P173-3 | `Rejected` com `PHOTO_REVIEW_REJECTED` para decisão humana, `PHOTO_REVIEW_EXPIRED` para espera vencida e `PHOTO_REVIEW_UNAVAILABLE` para candidato perdido. Motivos internos no caso; explicação pública acionável e localizada. | Códigos do resultado de revisão, não de `errors` ou da negociação ADR-0034. Requer contrato/CHECK próprios; não reutilizar `PROCESSING_LIMIT`. Categorias públicas/textos seguros ainda a detalhar. |
| P173-4 | Safety possui caso, atribuição e decisão; Media possui upload, arquivos e publicação. Moderador apenas em casos atribuídos, MFA, step-up e versão esperada na decisão. Auditar leitura antes dos bytes. ~~Recurso decidido por outra pessoa.~~ *(Substituído pelo adendo de 28/10/2026: sem recurso de fotos e um único revisor.)* | Operações administrativas e auditoria transacional futuras; falha da auditoria nega leitura. Operador/product_admin não recebe acesso implícito. |
| P173-5 | Alvo de primeira análise 24 h; espera expira em 72 h *(7 dias desde o adendo de 29/10/2026)*. ~~Recurso até 7 dias da rejeição; alvo de decisão em 7 dias. Candidato rejeitado retido por 7 dias; com recurso até encerramento + 24 h, limitado a 30 dias desde rejeição, salvo preservação excepcional explicitamente aprovada.~~ *(Substituído pelo adendo de 28/10/2026: sem recurso; o candidato rejeitado recebe a mesma obrigação de limpeza de 24 h da aprovação.)* Candidato expirado: limpeza em até 24 h. Original eliminado após derivação/commit, sem aguardar humano. Registro mínimo de decisão/auditoria: 1 ano, sem imagem/texto sensível. | Reconciliar política de privacidade, `retention_until`, exclusão e backups; não é conclusão jurídica. Detalhar exceções de preservação e reconciliação (o recurso vencido deixou de existir com o adendo de 28/10/2026). Alvos de atendimento não são evidência de capacidade. |
| P173-6 | Área privada `review-candidates`, separada de quarantine/derivatives e fora do backup de mídia; somente derivados sanitizados. Promoção com geração/versão e commit condicional, seguida de limpeza. Perda após restore: `PHOTO_REVIEW_UNAVAILABLE` e novo envio. *(Atualizado pelo adendo de 28/10/2026: a rejeição também grava a obrigação de limpeza do candidato, executada pelo `DecidedCandidateCleanupDispatcher`.)* | Montagens/permissões, leitura administrativa, GC e tombstones. Metadados dos casos seguem no backup do banco; perda de disco pode exigir reenvio. |
| P173-7 | ~~Modelo aceito: responsável pelo serviço, revisor principal e substituto capaz de julgar recurso independentemente.~~ *(Substituído pelo adendo de 28/10/2026: o dono do projeto é o responsável pelo serviço e o único revisor; não existe substituto.)* Sem cobertura, suspender novas admissões pelo procedimento aprovado e tratar pendências. | ~~Todos os nomes permanecem pendentes~~: nomeação resolvida pelo adendo de 28/10/2026. Sem recurso, a regra "uma pessoa não revisa a própria decisão" deixa de ter objeto. Indisponibilidade do dono continua exigindo a suspensão de novas admissões. |

A retenção aceita precisa ser reconciliada com [retention.md](../../privacy/retention.md) na entrega posterior.
Ela não muda neste PR jobs de limpeza ou backups imutáveis; não promete apagar cópias externas imediatamente.
Leitura/exclusão/limpeza de mídia exigem especificação antes de dados reais (o tratamento de recursos vencidos deixou de existir com o adendo de 28/10/2026).

## Entregas posteriores e dependências

- Aceite arquitetural registrado. Implementação requer autorização própria, detalhamento das entregas e resolução das condições aplicáveis abaixo; este PR é exclusivamente documental.
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
vazia. *(Atualizado pelo adendo de 28/10/2026: a allowlist `PRODUCTION_MEDIA_REVIEW_POLICIES` passou a ser
`{"HumanReview"}`; `duora_media_review` continua `""` e ligar o upload continua sendo da #69.)* Não liberar TechnicalOnly em Production/Staging, nem alterar limites, políticas executáveis, API,
segredos, infraestrutura ou ativação neste PR. Dados reais, deploy e homologação requerem autorização própria.
A #69 continua bloqueada pelas duas entregas, pela #86 e pelos gates B2/backup/restore/montagens/verify.

A futura implementação deve produzir a evidência de testes e homologação descrita acima, com dados
sintéticos, revisão humana e nove checks obrigatórios no SHA final. O CI deste PR valida a alteração
documental contra as suítes existentes; não comprova uma política humana ou sandbox ainda inexistentes.
Não executar builds, testes, Docker ou VM no Mac do responsável. Resultados do CI pertencem ao PR; nenhum
resultado futuro é presumido neste ADR. Issues #173, #174 e #69 permanecem abertas.

## Decisões de 28/10/2026

O dono do projeto decidiu em conversa, em 28/10/2026, os cinco pontos abaixo. Eles substituem as partes
indicadas acima (item 1 quanto aos ambientes, última frase do item 6, P173-1, P173-4, P173-5, P173-6, P173-7 e
a allowlist de "Segurança e operação"); o restante deste ADR continua vigente.

1. **HumanReview em Production, sem aprovação automática.** `MediaReview.IsPermitted`
   ([MediaReview.cs](../../../backend/src/Duora.Core/Media/MediaReview.cs)) admite `HumanReview` em
   Development, Testing e Production. Staging e nomes desconhecidos continuam recusados: Staging não foi
   decidido. `TechnicalOnly` continua só em Development e Testing. A allowlist
   `PRODUCTION_MEDIA_REVIEW_POLICIES` de [duora_deploy.py](../../../scripts/deploy/duora_deploy.py) passou a ser
   `{"HumanReview"}`. O upload em produção **não** foi ligado: `duora_media_review` continua `""` no inventário,
   e ligar fica para a [#69](https://github.com/BipoXDXD/duora/issues/69), com os gates de B2, backup, restore,
   montagens e verify e a #86.
2. **Sem recurso de fotos.** Não há nem haverá segundo revisor, então o fluxo de recurso da revisão de fotos foi
   removido: `POST /api/v1/me/photos/{photoId}/appeals`, todo o grupo `/api/v1/admin/photo-appeals` (lista,
   detalhe, `assignments`, `evidence`, `decisions`), a capacidade `appeal`, os campos `appeal`,
   `canRequestAppeal` e `isAppeal` do histórico, o `appealId` da decisão, as telas `/admin/photo-appeals` e o
   botão de recurso no histórico. Foto recusada continua `Rejected`; a pessoa pode enviar uma foto nova, que entra
   numa revisão nova, limitada só pelos rate limits e cotas existentes (3 pendentes por conta, 100 globais). A
   rejeição já grava a obrigação de limpeza do candidato privado, com a mesma janela de 24 h da aprovação,
   executada pelo Worker logo após a decisão (`DecidedCandidateCleanupDispatcher`, antigo
   `ApprovedCandidateCleanupDispatcher`). Antes, o candidato rejeitado ficava 7 dias à espera de recurso. O
   registro mínimo de decisão e auditoria continua de 1 ano, sem imagem nem texto sensível.
3. **Migração expand/contract.** `20261028120000_PhotoReviewWithoutAppeals` só expande: a constraint de retenção
   da decisão aceita a rejeição com 24 h e mantém os 168 h da release anterior; as rejeições antigas de 168 h
   recebem a obrigação de limpeza; e a migração falha se existir qualquer linha em `safety.photo_review_appeals`
   (nenhum ambiente persistente tem recurso, porque HumanReview nunca foi admitida fora de Development e
   Testing). Tabelas, colunas `appeal_*` e privilégios de recurso ficam no schema para o rollback à release
   anterior funcionar. O contract (remover tabelas, colunas e os 168 h) fica para uma migração posterior, depois
   que nenhuma release em uso as conhecer. Em rollback, a release anterior pode mostrar `canRequestAppeal` numa
   rejeição nova, mas o pedido é recusado porque o candidato já tem limpeza pedida; nenhum dado é perdido. O
   rollback só é seguro com o upload desligado, e o `duora-deploy` recusa ligá-lo em Production enquanto o ledger
   externo de exclusões (#69) não existir ([runbook](../../operations/backup-restore.md)). A migração usa
   `lock_timeout` de 5 s no ALTER da constraint.
4. **Um único revisor.** O dono do projeto é o responsável pelo serviço e o único revisor e moderador de fotos:
   papel `moderator` com as permissões explícitas da revisão (`assign`, `evidence`, `decide`), MFA e step-up
   (MFA recente) na decisão. `product_admin` e `operator` não leem fotos. Sem cobertura (dono indisponível),
   continua valendo suspender novas admissões pelo procedimento aprovado; não existe substituto.
5. **Continuam abertos** (não resolvidos por este adendo):
   - ~~exclusão de foto durante processamento ou revisão (hoje 409 `PHOTO_DELETE_STATE_CONFLICT`)~~: resolvida no
     PR seguinte da #173 (28/10/2026); só `Pending` ainda responde 409, e o candidato privado é limpo pelo tombstone
     ([media-review.md](../../api/media-review.md));
   - ledger externo e retenção dos registros de exclusão (tombstones e recibos): o ledger exige custódia B2 nova,
     que é da #69; o restore já falha fechado sem a barreira do ledger;
   - API-P04: a spec não define o contrato da foto no perfil ou na sala, e `GET /profiles/{userId}` ainda não
     existe;
   - aceite da política de privacidade pelo titular;
   - ativação do upload (#69).

A matriz de autorização das rotas que restaram está no
[contrato de revisão](../../api/media-review.md#matriz-de-autorização-28102026). O recurso de **moderação** de
denúncias e sanções (API-S06, API-ADM15, INV-10, `Appealed` em Denúncia) é outra feature e não muda.

Compliance: `HumanReviewIsAdmittedInSyntheticEnvironmentsAndInProduction` e
`HumanReviewIsNotAdmittedInUnknownEnvironments` (`HumanReviewIngressPolicyTests`);
`test_human_review_is_the_production_policy_but_the_inventory_keeps_the_upload_off` e
`test_review_outside_the_production_allowlist_is_refused` (`test_duora_deploy.py`);
`SingleModeratorRejectsThenANewUploadOpensANewReviewThatTheSameModeratorApproves`,
`AppealRoutesNoLongerExistForOwnersOrModerators` e `OwnerHistoryMasksOtherOwnersWithTheSameNotFound`
(`PhotoReviewHttpTests`); `ARejectionWrittenByThePreviousReleaseGainsTheDecidedCleanupObligation`,
`TheBackfillLeavesPendingAndApprovedCandidatesUntouched` e `TheMigrationRefusesADatabaseThatStillHasAnAppeal`
(`PhotoReviewWithoutAppealsMigrationTests`). O resultado desses testes pertence ao CI do PR.

## Decisões de 29/10/2026

O dono do projeto decidiu em 29/10/2026 as pendências P1–P5 da política `HumanReview`
([comentário na #173](https://github.com/BipoXDXD/duora/issues/173)). Este adendo não reescreve as seções
anteriores: altera só o prazo de espera do P173-5 e fecha a retenção que o item 5 das decisões de 28/10/2026
deixava aberta.

1. **P1. Espera da fila: 7 dias.** O caso em `AwaitingReview` vence em `awaitingReviewAt` + 168 h, não mais
   + 72 h (`PhotoReviewCases.ReviewWait`). Vencido, o `PhotoReviewExpirations` leva a foto a `Rejected` com
   `PHOTO_REVIEW_EXPIRED`, resultado do sistema; nunca a aprova. A meta de 24 h para a primeira análise continua.
   O candidato privado fica retido até o prazo + 24 h (antes, + 96 h desde a entrada), a mesma folga de limpeza
   que os coletores já exigem.
2. **Casos pendentes mantêm o prazo com que entraram.** O prazo é gravado por linha (`review_deadline`, com a
   retenção do candidato derivada dele). A migração `20261029120000_PhotoReviewSevenDayWait` só expande a
   constraint `ck_photo_review_cases_deadline` para aceitar 168 h e os 72 h da release anterior; nenhuma linha é
   reescrita. Alternativa descartada: estender os pendentes para 7 dias. Ela guardaria a foto de quem já está na
   fila por mais tempo do que o prazo vigente quando a pessoa a enviou, reescreveria tabelas de dois módulos
   (Safety e Media) numa migração e mudaria um `reviewDeadline` já mostrado ao revisor e lido por leases de
   evidência em andamento. Manter o prazo antigo não apaga nem aprova nada por si: no pior caso, um pendente
   expira em 72 h e a pessoa envia a foto de novo. Hoje o upload está desligado em Production, então os pendentes
   só existem em Development e Testing. O rollback continua seguro: a release anterior grava 72 h, que a
   constraint expandida aceita. O contract (só 168 h) fica para depois que nenhuma release em uso gravar 72 h e
   nenhum caso de 72 h estiver pendente. A constraint usa `interval '168 hours'`, não `'7 days'`, porque em
   `timestamptz` o intervalo em dias segue o horário de verão do fuso da sessão.
3. **P2. Sem Staging.** Não existe ambiente de Staging; a política continua só em Development, Testing e
   Production, como no item 1 das decisões de 28/10/2026. Nada muda no código.
4. **P3. Retenção de tombstones e recibos de exclusão: 1 ano**, igual à auditoria. Vale para
   `media.photo_tombstones` (desde `deleted_at`) e `media.photo_deletion_commands` (desde `created_at`). Um ano
   cobre com folga os ~65 dias em que uma cópia de backup ainda pode trazer de volta uma foto excluída. Não existe
   job de expurgo dessas linhas, e a aplicação não tem `UPDATE`/`DELETE` nelas; até o job existir, nada é
   apagado. O ledger externo continua aberto (#69).
5. **P4. Política de privacidade sem mudança.** O texto continua sem mencionar a revisão humana.
6. **P5. Upload em Production continua desligado** (#69); `duora_media_review` segue `""` no inventário.

Continuam valendo as decisões de 28/10/2026: só o dono revisa, como `moderator` com MFA; toda foto passa pela
revisão; o candidato é apagado 24 h após a decisão; o log de decisão fica 1 ano. Continuam abertos o ledger
externo, o job de expurgo dos registros de exclusão, a API-P04, o aceite da política pelo titular e a ativação do
upload (#69).

Compliance: `ReviewExpirationUsesTheExactDeadlineAndCommitsOnlyASystemResultAndCleanupObligation` (7 dias menos
1 tick não expira; 7 dias expira), `ANewCaseIsStillAwaitingReviewAtTheFormerSeventyTwoHourDeadline` e
`ACaseOpenedByThePreviousReleaseKeepsItsSeventyTwoHourDeadline` (`PhotoReviewExpirationTests`);
`CaseDeadlineIsSevenDaysOrTheSeventyTwoHoursOfThePreviousRelease` (`MediaReviewMigrationTests`);
`SanitizedCandidateCommitsReviewCaseAndOriginalCleanupWithoutAnApprovedAsset` (`ReviewCandidateStageTests`).
O resultado desses testes pertence ao CI do PR.
