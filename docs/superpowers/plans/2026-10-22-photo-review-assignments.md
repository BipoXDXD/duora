# Atribuição e metadados de revisão — M3b

Execução da ADR-0035/media-review, após M3a. Serviço interno não registrado; sem HTTP/UI, bytes, CLI ou ativação. INV-03/05/06/08/10/11; T-03/05/06/08/10/26. Preservar assets. Testes PostgreSQL e filesystem sintéticos somente na CI, sem Docker/builds no Mac.

1. Escrever regressões reais e observar RED: atribuição/versionamento/auditoria; replay depois de versão avançar, intenção divergente e dois comandos simultâneos; escopo/capacidades/conta/sessão; alvo revogado; 403 antes de consultar caso; reatribuição; paginação e DTO sem owner/path/hash/URL; falha de auditoria sem efeito.
2. Histórico e recibos append-only, novos e vazios. Caso/root version serializa atribuições; chave é hash por ator/operação, intenção fechada caseId/reviewerId (versão é precondição). Migração não altera registros/arquivos existentes.
3. Pré-autorizar em transação curta; replay confirmado pode retornar nela após validar ator/capacidade/corpo. Sem recibo, encerrar essa transação antes de descobrir envolvidos. Nova transação adquire todas as guardas em ordem, reautoriza, relê recibo antes das precondições mutáveis e então revalida caso/titular/alvo/candidato/prazo e expectedVersion. Conflito de lock aborta a transação inteira, sem storage/rede sob locks.
4. Atribuição não concede permissão e não renova prazo. Alvo exige moderator Active/email/MFA/senha definitiva e grants Evidence/Decide, sem exigir sessão ativa antes de começar a agir. Leituras exigem Evidence e atribuição atual; só metadados mínimos, default50/max100, cursor interno tipado. Bytes auditados/revogáveis são M3c, sem URL temporária como atalho.
5. Negativos de ator UUID maior que titular/alvo, revogação entre fases/espera e atribuições cruzadas concorrentes comprovam a ordem. Nenhuma mudança em M3a que torne autorização permissiva. Revisão independente, todos os checks no head/base finais e publicação somente automática após merge autorizado.

Não expor fila global de não atribuídos neste recorte: o filtro explícito precisa de contrato M5 e capacidade Assign específica. Decisões/recurso, retenção/restore e composição continuam M4/M6/M7. Luca Grimmer é responsável e principal; cobertura independente continua necessária para ativação. Nenhum grant real é criado por este plano.

## Evidência antes do produto

RED real em25f700a, CI39824303592/backend119983614407: build/formatação passaram;3314 testes,19 falhas somente nos novos cenários de atribuição,3295 aprovados,zero ignorados. be824c6(CA1861) e570a2a7(WHITESPACE) não foram contados como RED, pois pararam antes dos testes. Produto escrito somente depois dessa prova.

Reautorizar também DEPOIS de todos os locks de negócio: sessão pode vencer enquanto aguarda a linha do caso. Teste adicional bloqueia root em conexão independente, observa esperaPG e avança relógio até idle_expires_at antes de liberar; exige Forbidden sem histórico/recibo/audit. Esse teste adicional não faz parte da contagem do RED anterior.
