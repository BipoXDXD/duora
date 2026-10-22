# M4a1 — decisão humana inicial de rejeitar (proposta, sem código)

Escopo de um PR: somente rejeição humana inicial; não habilita admissão, HTTP/UI ou capacidade real. Depende de M3b e aproveita M3c apenas para as regras verificadas de contexto/locks/integridade, sem usar a auditoria EvidenceViewed como decisão. Aprovação/promoção e recurso serão recortes próprios. INV-03/05/06/08/10/11; T-01/03/05/06/08/10/26.

## Contrato e fronteiras

Serviço interno recebe ator validado, caseId, versão esperada, IdempotencyKey e motivo fechado. Código interno fechado does_not_meet_policy significa apenas que a pessoa decidiu que a foto não atende à política policyVersion existente, exclusivamente para reject; não aceitar texto livre/categorias inventadas, não alterar política de conteúdo humana existente. Persistir policyVersion/candidateGeneration e manifestos mínimos sem imagem/path; decisão não sanciona a conta. Fotografia continua exigindo revisão humana: nenhuma inferência técnica gera aprovação ou reprovação de mérito.

Proposta de histórico append-only safety.photo_review_decisions(id,case_id,case_version,actor_id,decision='reject',reason_code='does_not_meet_policy',candidate_generation,policy_version,decided_at,candidate_retention_until_snapshot,history_retain_until) + manifesto histórico por decisão/variante/hash/tamanho e recibos de comandos novos (não ampliar cegamente CHECK operation='assign' atual). Correlacionar auditoria com decision.id para duas decisões/aberturas não dependerem do mesmo relógio. O snapshot candidate_retention_until_snapshot=decidedAt+7dias descreve somente os bytes candidatos, não a retenção do histórico; history_retain_until é no mínimo decidedAt+1ano para decisão/audit/manifesto mínimo, sem imagem. M6 deve impedir expurgo antes desse piso e aplicar os prazos de bytes independentemente. ACL SELECT/INSERT, inventários exatos/reconcile na mesma fatia; tabelas inicialmente vazias. Composição futura adicionará approve e appeal por migração explícita, nunca flag permissiva ou operação desconhecida.

## Ordem e atomicidade

1. Autorizar Decide em RC antes de localizar caso. Replay confirmado exige autorização/TOTP atuais, mas precede versão mutável/estado e integridade de candidato que pode ter expirado depois da confirmação. Chave/hash inclui ator/operação e intenção canônica (caseId,decision,reason); expectedVersion fora da intenção. Chave reutilizada divergente rejeita sem efeito.
2. Encerrar pré-autorização antes de descobrir owner/generation. Na transação seguinte, todas Safety guards UUID ordenadas, depois Identity users/roles NOWAIT, upload/root/candidate/manifests. Reautorizar depois de todas esperas. Exigir atribuição atual, AwaitingReview, candidato committed+settled, uploadVersion coerente, retention/deadline vivos, titular email/status elegível. Snapshot com versões e manifestos canônicos completos.
3. Encerrar SQL/contexto. Confirmar as três variantes sanitizadas no storage, uma por vez, tamanho exato e SHA256, no máximo16MiB por variante/24MiB total, orçamento total15s. Sem original, derivado final, cópia ou decoder. Observar I/O/cancelamento, zerar cada buffer após hash. Ausência/corrupção devolve Unavailable, nunca uma rejeição humana registrada. M6 cuidará do status público dessa indisponibilidade.
4. Nova transação: repetir TODA autorização/ordem/guards e comparar snapshot/atribuição/versões/manifestações/prioridade de exclusão; relógio/TOTP capturados depois de todos waits. Replay confirmado por concorrente é verificado novamente antes das condições mutáveis. Só um vencedor para versão esperada.
5. Commit atômico: Upload AwaitingReview→Rejected/PHOTO_REVIEW_REJECTED e incremento da versão própria; root.version +1; candidato retention_until=decidedAt+7dias; histórico de decisão+manifesto; recibo confirmado; auditoria. Não apagar candidato já no commit nem marcar cleanup_requested: recurso válido pode estender retenção na mesma guarda até o prazo autorizado. Original já removido por M2. Não criar media_asset, mídia final nem sanção/role.
6. Se auditoria/SQL falhar, tudo rollback. COMMIT ambíguo não retorna decisão aplicada; repetição autorizada comprova recibo confirmado. Nenhuma tarefa pós-commit sem obrigação durável. ADR0035 decisão5 permite outbox somente se houver efeito real declarado; não criar email/evento fictício sem consumidor.

## Provas antes do produto

- Positivo: versions foto/caso independentes avançam uma vez, estado/código corretos, retenção dos bytes candidatos exatamente7dias e histórico mínimo por1ano distintos, política/geração/manifestos/ator/histórico/audit/receipt correlacionados. Zero media_assets/storage writes/deletes, assets sentinela e casos alheios idênticos.
- Decide sem Evidence não dá leitura de imagem ao cliente; decisão exige Decide+sessão/TOTP e atribuição, não interpretação de role privilegiado. Capacidades são separadas pelo contrato aceito: atribuir enquanto ambos os grants existem, revogar apenas Evidence e provar que Decide vivo pode registrar decisão, enquanto GET/Open de evidência continua Forbidden e nenhum byte chega ao chamador. Não exigir artificialmente EvidenceViewed anterior.
- Sem Decide/role/sessão/TOTP (incluindo recovery-only ou idade exata5min): Forbidden antes de consultar caso. Assign/product_admin/operator não dão autorização implícita.
- Não atribuído/inexistente uniforme NotFound. Variante ausente/short/long/hash inválido devolve Unavailable com zero decisão/audit/receipt; prova I/O não segura SQL.
- Mesmo comando repetido após avanço/expiração retorna resultado original somente com autorização viva; intenção divergente conflita; duas chaves com mesma versão: um vencedor. Mesmo key concorrente: um histórico, audit e receipt.
- Bloqueio real da guarda/root observado no PostgreSQL; TOTP/sessão/prazo expiram durante espera e efeito é negado. Restrição/deleção/reatribuição/versão/candidato limpo durante I/O impede commit; não recuperar locks fora da ordem ou reutilizar contexto transacional abortado.
- Falha de audit e falha de insert de histórico/manifesto/receipt dão rollback integral. Falha antes/depois COMMIT não produz resposta confirmada, mas somente o apósCOMMIT pode persistir histórico completo, comprovado por nova conexão.
- Nenhuma alteração de assets legados, nenhuma promoção/publicação, nenhuma flag HumanReview, nenhuma ativação/credencial/produção.

Esta proposta precisa revisão independente antes de testes. Não foi implementada nem aprovada como nova política de conteúdo. O bloqueio de nomeação independente continua sendo de ativação, não de código.

## Revisão de desenho
review_original_cleanup encontrou um P2: ambiguidade entre retenção de bytes7dias e histórico1ano; fechado nesta revisão com campos/regras/testes distintos. Razão genérica fechada e Decide independente de Evidence confirmados coerentes com contrato; nenhuma nova política/permissão real. Revalidar delta antes de testes/produto.

## Estado dos testes
Primeiro recorte local com testes e seam fail-closed. Sem produto, migrações, rotas, registro DI ou flags. A CI deve provar RED comportamental próprio; erro de build/format/fixture não conta. Provas adicionais de COMMIT ambíguo, espera real de guarda e rejeição concorrente com exclusão serão exigidas antes de produto. Todos os checks no SHA/base finais e liberação da fila são obrigatórios antes de merge.

## Prova comportamental inicial e reforços
RED e34f58e: CI39835489088/backend120022573063, build/formatPASS,3340total/21falhas exclusivamente novas/3319pass (3315baseline+4negativos),0skip. Ainda sem produto. Acrescentados testes antes de implementar: falha antes/apósCOMMIT (pós confirmado por outra conexão), TOTP vencendo em espera real observada de ownerGuard/root mantendo Evidence vivo, exclusão e reatribuição durante I/O sem bloquear SQL nem sobrescrever concorrente. Revisão desses deltas sem P1/P2; CI própria pendente.
