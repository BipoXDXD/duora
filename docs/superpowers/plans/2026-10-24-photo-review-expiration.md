# Photo review expiration — plano de implementação

> Execução nativa neste checkout pelo autor; revisão independente ao concluir. Escopo de engenharia/produto delegado, sem nova autorização genérica. Aplicar executing-plans e TDD. Sem delegar implementação ou repetir evidência válida.

**Goal:** expirar a espera72h como resultado System e limpar somente seu candidato confirmado por obrigação auditada.
**Architecture:** coordenador Safety transacional + coletor Media separado; sem DI/HTTP/ativação atéM7. Preservar todos os escritores/leitores anteriores e o pacote125v2.
**Tech Stack:** .NET10/EF/Npgsql/PostgreSQL18, filesystem sintético e xUnit; stdlib local apenas para documentação/bindings.
**Spec:** docs/superpowers/specs/2026-10-24-photo-review-expiration-design.md.

Esta branch começa pelo recorte interno M6b1: transição lógica72h e obrigação auditada. A coleta física M6b2 será testada depois que esse coordenador for qualificado; os dois recortes juntos precisam cumprir o teto24h antes doM7. Nenhuma entrega parcial encerra173/69.

## Restrições e foco da revisão

- 72h é limite exatoUTC, limpeza até24h; nunca fabricar decisão humana, Approved, recurso ou sanção.
- Ownerguard→IdentitySHARE→upload→case→candidate; sem SQLlocks durante storage e sem transaçãoambiental.
- Snapshot descoberto antes dos locks é não autoritativo: estado/geração/versão/prazo precisam ser rechecados após espera.
- EventoSystem/versão e cleanup_audit_idbigint na mesma transação; falha de auditoria/COMMIT não produz confirmação falsa.
- Coleta só das três chaves canônicas com token/lease60s/DELETE15s/backoff; nenhum original/final/outro candidato/histórico apagado.
- Sem novos grants/tabelas, ACLlegada, backfill, processos/serviços, flags ou matrículas por enumeração. Migração aditiva própria e oito heads preservados.
- Testes novos e gates só hospedados; zero SDK/build/format/test/Docker/SSH no Mac. Não integrar main sem slot69c1.

## Task1 — contrato executável antes do produto

Arquivos: novos PhotoReviewExpirationTests.cs e PhotoReviewExpirations.cs scaffold, no namespaceSafety; usar a fixture real existente de PhotoReviewApprovalTests partial sem mudar helpers já qualificados.
Interface: PhotoReviewExpirations(IDbContextFactory<DuoraDbContext>,TimeProvider).ExpireOnceAsync(CancellationToken)→Task<int>0/1.

- [ ] Teste boundary: CaseAsync→advance72h−1tick→ExpireOnce=0→advance1tick→ExpireOnce=1. Exigir uploadRejected/PHOTO_REVIEW_EXPIRED, versão+1, caso+1, marker/eventoSystem; zero decisões/assets.
- [ ] Testes de duas execuções/duas instâncias, estados já finais, divergência de geração e rollback de auditoria. Nenhum teste compara só texto ou mock.
- [ ] Expiração aguardando ownerguard cruza deadline; relógio após lock prevalece. Aprovação/rejeição/exclusão concorrentes não perdem dados nem criam dois efeitos.
- [ ] Fault de COMMIT antes/depois exige retry que observa persistido; resposta perdida nunca duplica audit/versões.
- [x] Revisão independente dos testes antes do RED: quatro P2 de cobertura corrigidos (COMMIT, revalidação após guarda, projeções completas, cancelamento/observação do produtor); re-review sem novos P1/P2 por inspeção. São12casos preparados, ainda não executados.
- [ ] Publicar RED somente depois do backend235corrigido validado e execuçãoanterior concluída; falha de build/format/infra não contaRED. Não iniciarCIextraenquantoaindaalteramososmesmostestes.

## Task2 — implementar após RED válido

- [ ] Novo AuditAction.PhotoReviewExpired, sem alterar o caminho AuditTrail.AppendAsync. ExpireOnce usa contextos própriosREADCOMMITTED; descobre e guarda/revalida/CAS segundo a spec.
- [ ] Migração aditiva cleanup_audit_idbigintFKops.audit_events e índice parcial. Consulta única do evento por System/action/case/version na mesma transação, nuncaMAX/IDinventado.
- [ ] Rollback integral se contagemCAS/evento não for exata; commit ambíguo propaga indisponibilidade e retrysemreaplicar.
- [ ] Manter o coordenador interno sem DI nesta fatia; M7 liga consumidores somente depois da composição completa aceita.

## Task3 — qualificar e entregar

- [ ] Revisão independente dirigida do delta; corrigir P1/P2 e declarar quais provas adicionais não constavam noRED.
- [ ] UmaCIdeproduto necessária com gates/nativos aplicáveis; preservar falhas herdadasM2 como bloqueantesglobais, semduplicarcorrigir oudispensar.
- [ ] Relatório com SHA/base/arquivos/hashes, testes reais executados/nãoexecutados, limites, rollback e próximos gates. Não encerrar173/69 por esta fatia parcial.

## Task4 — M6b2, coletor depois do coordenador qualificado

- [ ] Adicionar contrato/scaffold ExpiredCandidateCleanupDispatcher com teste integrado expiry→GC e RED próprio. Preservar arquivos/dados; vínculos divergentes recusados; parcial/backoff/shutdown/lease vencido antes/depois de trocar token.
- [ ] Implementar prova auditada e marker devido, token/lease/backoff, storage foraSQL. Não ampliar os coletores de abandonados/aprovados nem ativar admissão/rotas.
- [ ] Qualificar e revisar o recorte e a composição; somente depois avaliar consumidores M7 e prova de limpeza em24h.
