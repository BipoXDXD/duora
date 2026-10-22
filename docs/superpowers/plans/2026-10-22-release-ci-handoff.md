# Identidade de CI entre gate e publicador reutilizável

Correção delimitada do fluxo de publicação aprovado. Executar inline com TDD no Actions e revisão fresca única; nenhuma suíte/Docker no Mac. Evidência:39804616851 passou gate para597dccf, mas images/verify não encontrou o CI39802161001 no índice. Não é autorização para contornar checks pendentes nem manipular execuções.

1. Testes exercitam o programa check_ci por API sintética: execução conhecida funciona mesmo com índice vazio; par parcial/inválido, identidade/attempt divergente, job incompleto/não verde e rerun durante leitura falham sem outputs. Preservar consulta por commit quando não há par. Observar RED.
2. Receber CI_RUN/CI_ATTEMPT opcionais como par; consultar run diretamente, verificar identidade/push/main/workflow/repo/commit, nove jobs do attempt exato e reler execução antes dos outputs. Preservar a exigência de ancestral da main. Nenhum retry/timeout/relaxamento novo.
3. Em alteração dedicada de workflows, gates expõe ci_attempt e passa par ao workflow reutilizável; verify mapeia inputs para ambiente. Testes de ligação e revisão do responsável técnico sob autorização vigente de merge após checks.
4. Nove checks + verificadores dos workflows, revisão independente única e uma passada P1/P2 com RED/GREEN; integrar somente após janela188 e atualização com main. Acompanhar promoção automática sem cancelar/rerodar. Metadados ainda pendentes continuam bloqueando.

Foco: nenhuma referência fornecida dispensa validação, tentativa antiga não qualifica rerun, falhas não escrevem outputs, permissões/filas/scanners/deploy e assets permanecem exatamente os existentes. Custo residual: APIs não são snapshot atômico; verificação final limita corrida, sem garantia absoluta após última leitura.
