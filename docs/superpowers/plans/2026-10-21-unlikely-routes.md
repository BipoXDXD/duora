# Issue #100 — Rotas Improváveis operacional

## Objetivo e limites

Estender a fatia entregue no PR #95 para Rotas Improváveis: publicação, persistência, sala HTTP e SPA,
recuperação e conclusão bilíngue sem microfone. Reutilizar pareamento, idioma combinado, aceite bilateral,
guardas, reservas e jobs. INV-05 (privacidade), INV-06 (reenvio), INV-09 (versão fixada), API-R01/R02/R03/R04 e
ADR-0020/0021 orientam a entrega. Não alterar região, chat, voz nem a elegibilidade vigente (ADR-0029).
O catálogo editorial real continua na issue #101.

## Fatias de implementação

1. Snapshot v1 no Core, com estado público, índice privado da pista e recibos sem payload; fixture JSON de
   compatibilidade e testes de restauração, inclusive versões inválidas.
2. Migração aditiva: discriminador imutável do catálogo, padrão Constelação para dados existentes.
   Publicação administrativa de Rotas com estrutura PT-BR/EN-US equivalente e áudio introdutório público.
3. Despacho por tipo nas salas, publicação de eventos, ações e prazo. A autorização precede replay;
   gravações usam a ordem existente de locks e a versão única da sala. Sorteio ocorre uma vez e seu
   resultado permanece no snapshot. Uma revisão é permitida nesta versão das regras operacionais.
4. Contratos aditivos e interface para introdução, pistas individuais, escolhas alternadas, revisão e
   confirmação bilateral. `routesGame` é nulo em Constelação; `game` é nulo em Rotas. Áudio por gesto.
5. Testes PostgreSQL de publicação, concorrência, recuperação, privacidade, bloqueio e prazo; E2E real
   PT-BR/EN-US sem chamada de microfone; regressão das suítes existentes e contratos regenerados.

## Revisão e operação

Revisão humana obrigatória antes de merge por envolver migração, autorização e persistência. Migração
não destrutiva: conteúdo antigo recebe `constellation`; não reescrever migrações anteriores. Para retirada,
interromper novas publicações de Rotas e drenar salas com o binário compatível antes de voltar ao binário
anterior. Não usar downgrade de schema nem iniciar servidores antigos contra salas Rotas já publicadas.

A mídia desta fatia reutiliza o transporte WAV do PR #95; o contrato aceita apenas a cue pública `intro`,
com transcrição igual ao objetivo e descrição igual ao título em cada idioma. A revisão editorial deve
conferir o áudio real contra a transcrição. As fixtures silenciosas não são conteúdo de produção. A evolução
editorial/multimídia completa do ADR-0027 permanece separada; não se declara o catálogo real concluído.

A observabilidade reutiliza resultados HTTP genéricos e jobs duráveis; não registrar pistas nem corpos de
preferências. Evidências efetivamente executadas e limitações serão registradas no relatório da entrega.
