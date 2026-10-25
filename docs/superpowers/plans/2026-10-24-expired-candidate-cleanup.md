# Implementação M6b2 — candidatos expirados

1. Preparar testes PostgreSQL/filesystem e scaffold sem coleta, na branch própria filha239/2814b6a. Revisão independente antes de publicar. Não implementar produto antes de RED comportamental hospedado.
2. Após qualificação runtime239 e término de seus jobs, publicar draft filho com head fixo; confirmar build/formatação e falhas exclusivamente esperadas no scaffold. Não reexecutar manualmente jobs ou reduzir gates.
3. Implementar seleção estrita, claim SKIP LOCKED, token/lease, DELETE foraSQL, ACK e retry/backoff. Revalidar vínculos/prova em claim e ACK; nenhuma decisão, grant, tabela ou backfill.
4. Qualificar CI normal no head produto. Revisar de forma independente. Registrar comandos/resultados/logs e separar falhas herdadas de regressões. Conectar ao Worker de limpeza existente somente no produto qualificado; nenhuma admissão real.
5. Continuar recurso/retenção, DELETE/tombstones/asset, backup consistente/restore, HTTP/UI e M7. Composição/merge somente base final/gates/revisão e slot exclusivo69c1; não concluir173/69 pelo GC parcial.

Sem SDK/Docker/build/testes no Mac; dados sintéticos e CI hospedada. Sem alterações em #125v2, branches M2, políticas de segurança ou serviços reais.
