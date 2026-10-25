# M6a — limpeza de candidatos já aprovados

Objetivo: cumprir a obrigação de limpeza registrada atomicamente pela aprovação inicial, sem modificar o coletor de preparações abandonadas, a decisão, o asset final ou a política de admissão. Escopo humano delegado em 24/10/2026; execução neste checkout próprio, sem nova aprovação genérica.

Base: #226/e298d443; candidata canônica970b8549 preservada em sua branch. Esta fatia não publica a base operacional provisória nem autoriza instalação, ativação, contas ou grants.

## Contrato

Coletor separado ApprovedCandidateCleanupDispatcher; consumo de marcador explícito e devido em candidato committed/writer-settled, com decisão append-only approve/meets_policy da mesma geração/caso/política e retenção registrada. Nunca matrícula por idade, enumeração de disco ou estado Approved isolado. Somente as três chaves canônicas review-candidates/{generation}-{kind}; storage fora da transação SQL. Claim com SKIP LOCKED, token UUID, lease60s, DELETE15s, backoff persistido; encerramento exige token/lease vigente. Reinício pode repetir DELETE idempotente, nunca remover obrigação por falha ou perder decisão/auditoria. Sem logs de geração/titular/chaves/imagem.

Marcador de aprovação revoga evidência imediatamente;24h é teto de limpeza, não retenção mínima. Rejeição/recurso/expiração/hold/restore são próximos recortes e não serão inferidos nesta coleta.

## Execução e provas

- [x] Testes escritos antes do produto; scaffold sem coleta, para RED comportamental hospedado.
- [x] PR235 empilhado em #226; RED551fcbc comprovado em CI40139058528/backend121006691791: build/formatação aprovados,3563total/5falhas somente novas/3558sucessos/0skip. Log integral verificado, sem execução no Mac.
- [x] Implementar coletor/índice aditivo e consumidor Worker, preservando defaults desligados e GC antigo. Revisão estática independente sem P1/P2 após correções das lacunas de teste.
- [ ] Provar exclusão só da geração, preservação byte a byte de finais/original/outra geração, histórico/recibo/replay; pendente/rejeitado recusados mesmo com marcador.
- [ ] Provar falha parcial/backoff, shutdown sem ACK, retomada após lease e rejeição de token antigo; storage sem locks SQL longos.
- [ ] Obter nove gates finais/revisão independente e bindings; não integrar main sem janela69c1.

Falhas alvo: coletar outro candidato; confundir preparação com decisão; marcar deleted antes de concluir os DELETEs; ACK obsoleto; alargar acesso/TTL ou coletar sem marcador; tornar cleanup dependente de novas admissões. Testes usam PostgreSQL e filesystem sintéticos reais; fault injection só no I/O de exclusão. A composição final da stack e os nove casos ACL continuam uma qualificação própria após base final.

As seis provas adicionais (teoria de cinco vínculos e teste de lock durante DELETE) e a asserção de lease expirado antes da troca do token vieram da revisão independente após o RED original. Não integram a contagem das cinco falhas anteriores; sua execução pertence à validação seguinte.

O consumidor compartilha o serviço hospedado de cleanup existente, mas conserva dois dispatchers/queries e namespaces de posse distintos. Não há novas tabelas, grants, backfill, enumeração de arquivos, worker/porta adicional nem ativação de admissões. A migração só acrescenta índice parcial sobre as obrigações confirmadas.

Limites da primeira execução: supply-chain e imagens falharam em vulnerabilidades herdadas da basee298 (undici/filelock/Brotli). Os fixes pertencem ao M2 e não foram duplicados ou dispensados nesta fatia. Qualificação global exige sua incorporação pela base final coordenada.

Referência de método para PostgreSQL descartável e lifecycle de fixture: [Test-driving a Storage Adapter](https://dometrain.com/take/course/from-zero-to-hero-test-driven-development-tdd-csharp-2732006/test-driving-a-storage-adapter-54128362/). O projeto continua usando seus fixtures Testcontainers existentes; nenhuma nova dependência foi adicionada.
