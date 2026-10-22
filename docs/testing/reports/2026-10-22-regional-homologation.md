# Homologação técnica regional — etapa 10 da #108

Base de trabalho: `ad133bb57c63af99f70122be048d05af0459a55b` (etapa 9 integrada).
Escopo: documentação de contratos/dados/privacidade, matriz de invariantes e roteiro de implantação/reversão;
novo ensaio PostgreSQL com 10.000 contas/perfis, contenção da migração, retry e recusa de Down preservando escolhas.
Nenhum código de produto, configuração, dependência, migração, seed ou workflow alterado.

## Verificação

Local: `git diff --check`, leitura de diff/caminhos e revisão. Nenhum build, restore, Docker ou teste pesado local.
CI: os nove jobs existentes executam restore/build/format/test backend, frontend, e2e, contract, ops, secret-scan,
supply-chain e imagens AMD64/ARM64. Resultado do head será registrado no PR após a execução; esta frase não
é evidência de aprovação. O teste emite `regional-rehearsal rows=10000 lock-timeout-ms=...` e `expansion-ms=...`
no output xUnit e conserva as medições bem-sucedidas no resumo do job CI (`GITHUB_STEP_SUMMARY`). A duração inclui EF/processo, não mede exclusivamente tempo de lock nem SLA de produção.

## Limites e decisões de execução

- Teste de caracterização de contratos já implementados; não há comportamento novo de produção nem RED/GREEN fabricado.
- Volume de 10.000 perfis sintéticos é reproduzível e limitado ao CI; não comprova lotação/throughput ou capacidade do host real.
- Reversão ensaia recusa de Down e preservação de dados, não execução de binário antigo. Recuo operacional exige pausa e revisão própria.
- G2/G3/G4/G5 e autorizações de ativação continuam pendentes conforme o runbook; CI não homologa IPinfo/Gmail/storage/SLA.
- Sem mudanças protegidas, coleta adicional, EUA, detector/atualizador ativos, alerta real ou fechamento da issue.
- Nenhuma tentativa de instrução não confiável identificada.

As decisões evitam confundir homologação técnica com aceite operacional; o custo é manter a ativação bloqueada
até ensaios e aprovações específicos. Titular e Gmail privado preservados; não publicar endereço; ausência de
substituto/contingência/cobertura permanece limitação expressa.
