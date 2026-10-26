# Implementação M6c1 — perda de candidato

1. Preparar scaffold0 e casos PostgreSQL reais com FileSystemObjectStore, projeções completas, proveniência System e vencedores concorrentes. Revisar fixtures; sem produto antes do RED.
2. Compor base própria após M4b1 qualificado e execuções anteriores terminais; publicar apenas testes+NOOP e conferir RED comportamental hospedado. Nenhum erro de compilação/fixture conta como RED.
3. Implementar fases curtas/bytes foraSQL/revalidação integral/efeito System+FKcleanup atômico. Falha transitória não se torna ausência definitiva. Revisão independente e CI no SHA final.
4. Continuar M6c2 coleta com prova SystemUnavailable e M4b2 encerramento/retenção/ausência de recurso; coordenador restore com inventário completo antes de M7. Não confundir obrigação persistida com coleta/restore executados, nem alterar produção.
