# M6a — limpeza de candidatos já aprovados

Objetivo: cumprir a obrigação de limpeza registrada atomicamente pela aprovação inicial, sem modificar o coletor de preparações abandonadas, a decisão, o asset final ou a política de admissão. Escopo humano delegado em 24/10/2026; execução neste checkout próprio, sem nova aprovação genérica.

Base: #226/e298d443; candidata canônica970b8549 preservada em sua branch. Esta fatia não publica a base operacional provisória nem autoriza instalação, ativação, contas ou grants.

## Contrato

Coletor separado ApprovedCandidateCleanupDispatcher; consumo de marcador explícito e devido em candidato committed/writer-settled, com decisão append-only approve/meets_policy da mesma geração/caso/política e retenção registrada. Nunca matrícula por idade, enumeração de disco ou estado Approved isolado. Somente as três chaves canônicas review-candidates/{generation}-{kind}; storage fora da transação SQL. Claim com SKIP LOCKED, token UUID, lease60s, DELETE15s, backoff persistido; encerramento exige token/lease vigente. Reinício pode repetir DELETE idempotente, nunca remover obrigação por falha ou perder decisão/auditoria. Sem logs de geração/titular/chaves/imagem.

Marcador de aprovação revoga evidência imediatamente;24h é teto de limpeza, não retenção mínima. Rejeição/recurso/expiração/hold/restore são próximos recortes e não serão inferidos nesta coleta.

## Execução e provas

- [x] Testes escritos antes do produto; scaffold sem coleta, para RED comportamental hospedado.
- [ ] Abrir PR empilhado em #226 com RED no backend real; nenhum build/teste/Docker no Mac. Não interpretar falha de compilação/formatação como RED.
- [ ] Implementar coletor/índice aditivo e consumidor Worker, preservando defaults desligados e GC antigo.
- [ ] Provar exclusão só da geração, preservação byte a byte de finais/original/outra geração, histórico/recibo/replay; pendente/rejeitado recusados mesmo com marcador.
- [ ] Provar falha parcial/backoff, shutdown sem ACK, retomada após lease e rejeição de token antigo; storage sem locks SQL longos.
- [ ] Obter nove gates finais/revisão independente e bindings; não integrar main sem janela69c1.

Falhas alvo: coletar outro candidato; confundir preparação com decisão; marcar deleted antes de concluir os DELETEs; ACK obsoleto; alargar acesso/TTL ou coletar sem marcador; tornar cleanup dependente de novas admissões. Testes usam PostgreSQL e filesystem sintéticos reais; fault injection só no I/O de exclusão. A composição final da stack e os nove casos ACL continuam uma qualificação própria após base final.
