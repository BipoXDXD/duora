# Implementação M4b1 — solicitar recurso

1. Revisar contrato do titular/decisão7d/retention30d, sem ampliar privilégios editoriais. Preparar scaffold interno e testes reais antes de schema/produto; não expor rotas.
2. Depois da qualificação/encerramento das execuções anteriores e composição da base própria, publicar RED hospedado. Nenhum teste inválido por tabela inexistente ou erro de fixture conta como RED.
3. Implementar migrações aditivas/root/recibo/appeal_idnullable no histórico e UNIQUEcase, autorização viva e duas fases sem I/O sob lock; request+extensão+versões+auditoria atômicos. Rejeitar estados não recorríveis, decisões de recurso como origem e snapshots divergentes. Preservar o fato da rejeição inicial e não reiniciar janelas por recurso encerrado. Leitor reconhece appeal_id antes do escritorM4b2.
4. Revisão independente, CI normal e provas negativas/concorrentes no SHA final; integrar ACL canônica226 para os novos objetos antes de qualquer instalação. Sem alterar migrações aplicadas, políticas ou gates.
5. Continuar M4b2 com atribuição/leitura/decisão de recurso por humano distinto, encerramento e retenção limitada; M6 com coleta coordenada/ausência/restore. HTTP/UI e M7 somente após fluxo completo qualificado. Nenhum encerramento173/69 por pedido parcial.
