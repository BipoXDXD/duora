# ADR-0038 — Integração operacional IPinfo e alertas Gmail

Status: Proposed. O titular autorizou integração/ativação e escolheu Gmail com senha de app em 22/10/2026.
A revisão do código, checks e evidências operacionais permanecem obrigatórios.

O ADR-0033 definiu leitor, atualização, retenção e contrato de notificações separados dos hosts. A integração
agora registra componentes opt-in; sem configuração válida, o detector continua desligado e nenhum worker
operacional é registrado. O ADR-0037 conserva produto, schema, fonte e licenças. EH tem exceção ISO explícita
porque não possui cultura regional no ICU Linux homologado; isso não amplia o catálogo de perfis.

SMTP não fornece chave de idempotência durável. O adaptador reserva cada Id de notificação no PostgreSQL
antes de enviar e confirma aceitação depois. Aceitação confirmada torna chamadas repetidas sem efeito.
Após reserva sem confirmação, não há reenvio automático: pode ter havido envio antes da interrupção. O evento
continua pendente e conserva a ordem do incidente. O operador deve conferir o Message-ID exato no destino
antes de reconciliar. Isso evita duplicatas automáticas, mas pode exigir intervenção para uma mensagem que
não saiu. Não há promessa de exactly-once nem confirmação de leitura pela pessoa. Falha é registrada sem
credenciais/endereço/payload; o alerta de transporte bloqueado aparece nos logs operacionais.

A cópia usa SDK S3 já aprovado, bucket privado dedicado fora do host, sem Object Lock nem replicação. Um
bucket com imutabilidade21d é incompatível com exclusão14d: não se enfraquece o backup do banco para reutilizá-lo.
Objetos por hash conservam bytes, manifesto e atribuição; cópia repetida não cria nova versão. Retirada apaga
versões, marcadores e uploads incompletos e confirma ausência. A provisão deve provar ACL/retencão/exclusão no
fornecedor real; testes MinIO não substituem isso. A falha de cópia é durável e gera incidente Backup no monitor.

Manutenção usa role específica, pool2; Worker comum fica no máximo8, preservando a fatia total10. Web lê volume
somente leitura. Refresh usa geração/manifesto confirmados fora das requisições e não reabre base idêntica em
cada poll; idade e recuo de relógio continuam conferidos localmente. Monitor e manutenção têm loops separados
do atualizador e preservam fallback manual. Nenhum IP ou dado de participante entra em SQL, logs ou arquivo.

Revisar esta decisão ao trocar transporte, política de ambiguidade, bucket/replicação, limites ou governança.
