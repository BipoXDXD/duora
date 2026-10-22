# Capacidades explícitas de revisão — M3a

> Execução incremental da ADR-0035 e do contrato media-review. Núcleo interno, sem rotas, DI, CLI, concessões reais ou ativação. Depende de #205; a atribuição e leitura auditada continuam obrigatórias em M3b.

**Objetivo:** separar o papel moderator da permissão de revisar; revalidar conta, papel, concessão e sessão na transação do futuro efeito. INV-03/05/06/08/10/11; T-03/05/06/08/10/14/26. Dados sintéticos e testes no GitHub, sem Docker/builds no Mac.

1. Escrever regressões PostgreSQL para permissões independentes, concessão/revogação/reconcessão auditadas e sem duplicação; identidade/sessão/MFA inválidos; rollback de auditoria e concorrência real de revogação. Observar RED na CI antes do produto.
2. Criar tabela Safety nova, vazia e append-only com histórico por conta/capacidade, sem backfill. Catálogo fechado assign/evidence/decide/appeal/preserve. Concessão exige moderator Active confirmado, MFA e senha definitiva. Revogação não depende de a conta ainda satisfazer esses requisitos. Estado igual é no-op.
3. Implementar serviço interno não registrado. Grant/revoke exige operador terminal identificado e transação própria; actor(userId,sessionId) da borda autenticada será validado no núcleo de autorização, que exige transação externa. Guarda Safety, linhas de conta/papel/sessão mantêm exclusão com revogadores até o commit; avaliar tempo após locks. Nada de I/O de storage nessa transação.
4. Autorizar somente grant enabled mais recente + moderator + conta Active/email/MFA/senha definitiva + sessão própria Administrative, stamp vigente, não revogada, não expirada e prova MFA passada. Decide/Appeal/Preserve exigem TOTP em [agora-5min,agora], respeitando janela semiaberta existente. Capacidade sem atribuição não permite evidência; não reutilizar esse serviço sozinho como leitor.
5. Conferir histórico/auditoria append-only, falha de audit sem efeito, revogação concorrente, privilégios exatos e assets inalterados. Revisão de segurança antes de integrar; todos os checks do head final precisam passar. Publicação somente pelo fluxo automático.

Luca Grimmer foi indicado como responsável pelo serviço e revisor principal. Não há cobertura independente para julgar seus recursos; isso permanece impeditivo da ativação. Nenhum nome/conta recebe grant por esse registro. M3b/M4–M7 e os critérios operacionais de #173 continuam pendentes.

Revisão pré-código: recusar transações diferentes de READ COMMITTED para evitar snapshot anterior a revogação append-only. A leitura bloqueante de papéis após users usa FOR SHARE NOWAIT: concorrência com o trigger D13 aborta a transação inteira em vez de criar ciclo; não converter esse erro em acesso autorizado/negado com transação inválida. Cobrir UPDATE real de user_roles, não somente DELETE.

RED observado em2637aec, CI39815821567:3293testes,41falhas comportamentais novas,3252aprovados,zeroignorados. O REDanterior165era42601nafixture e não foi contado. Implementação interna preparada depois dessa prova; aguarda revisão e CIverde, sem DI/CLI/rotas ou grants reais. Histórico append-only mantém versões e auditoria; autorização exige READ COMMITTED e obtém tempo após locks, com TOTP derivado das capacidades Decide/Appeal/Preserve. Atribuição, evidência e composição seguem pendentes.
