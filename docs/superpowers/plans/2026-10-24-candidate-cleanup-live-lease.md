# Regressão — lease de limpeza após espera por rowlock

FindingP2 na revisão244: ApprovedCandidateCleanupDispatcher e ReviewCandidateCleanupDispatcher herdados capturam relógio antes do UPDATE que pode aguardar lock. ExpiredCandidateCleanupDispatcher já corrigido no244fbcfee9; sua qualificação não valida os pais.

1. Branchprópria filha244, seis casos novos antes da correção: ACK/retryApproved e Abandoned com writer conhecido/incerto, claimemT0, rowlockrealem59s, Fake+2depoisdepg_blocking_pids, recusa integral e retomada. Reusar helper já revisto do244 com cancelamento/await do produtor. Expor apenas retornointernalbool do RetryApproved sem mudar relógio/predicado/efeito: seamdeobservação, não fix.
2. Após244qualificado e execuções encerradas, publicar testes paraREDhospedado do bug real. Esperadas seis falhas deACK/retrytrue; nenhuma falta de tabela/fixture/compilação conta comoRED. Não alterar235/239remotos ou qualificação antiga.
3. Somente apósRED, alinhar ACK/retry dos dois coletores ao contrato járevisto: transação curta, lockcandidato, então reler relógio e revalidarlease/token/contexto; COMMITforaI/O. Manter semântica de writerincerto sem ACKterminal e sem expirar obrigação.
4. Revisão independente/CI normal no head produto, gates finais e composição226 preservando ancestry. Sem schema/grant/flags/workflow/SDKtestMac/SSH/produção.235não fica ready enquanto esseP2/gatesglobais persistirem.
