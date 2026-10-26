# Pacote de aceite regional para o operador

Preparado em24/10/2026; **não executado no host**. Operador exclusivo:69c1, após a passagem registrada na
fila compartilhada. Este pacote não reserva janela, autoriza ativação nem substitui os runbooks
[regional-matching](regional-matching.md), [ipinfo-runtime](ipinfo-runtime.md) e [ipinfo-country](ipinfo-country.md).
Desenho/decisões e medições reais estão no [dossiê](../testing/reports/2026-10-24-regional-product-acceptance.md).

## Entrada e identidade

1. O integrador fornece main/head final, run/tentativa e os nove jobs próprios SUCCESS. CI de PR anterior
   não qualifica nova composição. Registrar digests/release de Web, Worker e SPA, schema e políticas aplicadas.
2. Confirmar com a fila a janela e o escopo exato: coleta de leitura, ensaio isolado ou ativação. São operações
   diferentes. Preservar serviços, backups, coletores, alertas e limites vigentes.
3. Reutilizar o preflight233 sanitizado de22:26:35UTC, SHA256
   `8de46905bcf64ce0d728ee2cc370a1d7c287c55e4207ff956855460194fb5079`, para o que já provou.
   Ele contém29 identidades/mounts/config hashes; não contém NTP, grants regionais, bucket dedicado ou jobs.
   Mudança posterior de release/config exige somente a diferença pertinente, sem recaptura geral por hábito.

## G3 — coleta mínima ainda necessária

| Objeto | Recibo sanitizado exigido | Impede o aceite |
|---|---|---|
| Relógio | instante UTC e estado/sincronismo/offset do host e processos Web/Worker, fonte e momento da coleta | apenas horário do runner ou container sem vínculo com relógio do host |
| Recursos | memória/CPU/PIDs efetivos de cada consumidor, espaço/inodes do volume privado e envelope simultâneo aprovado pela frente de capacidade | somar picos não simultâneos ou usar alocação cumulativa como RAM |
| Configuração | quatro flags CountryDatabase e Participation efetivas por processo; caminho Root e RO Web/RW Worker; versão/digest e mecanismo de propagação | inferir valor efetivo de appsettings/Compose ou singleton recarregado sem restart |
| Manutenção | role `duora_geo_maintenance`, privilégios/ausência de herança ampla, pool2 e pool comum Worker<=8 | usar app/migrador como substituto ou considerar pg_hba prova de criação/grants |
| Storage | quota gratuita suficiente/custo incremental zero, bucket privado dedicado, prefixo/grants mínimos, sem Object Lock/replicação | reutilizar bucket imutável de backup, contratar ou ampliar gasto implicitamente |
| Entrega | Gmail privado sob custódia local, Message-ID e recibo no destino correlacionados com journal/estado | apenas aceitação SMTP, fila vazia, fixture pickup ou segredo exposto |

Coletar somente valores públicos/booleanos, identidades e hashes necessários. Não copiar Compose/env completo,
conexões, endereço Gmail, chaves, cookies, perfis, IPs de participantes ou a MMDB para o dossiê.
Se não houver quota gratuita ou capacidade, manter o componente desligado e registrar o impedimento.
Não executar DDL nem mudar credenciais para obter um inventário de leitura.

## G4 — inventário e corte manual

O filtro manual V3 não depende do detector. #228 já traz14 casos HTTP/PostgreSQL; #236 acrescenta a recusa
de publicação durante MatchingEnabled=false. O PR236 aprovado em CI ainda precisa de integração e entrega
para que essa guarda exista no alvo.

Na janela autorizada, o operador pausa admissão, drena requisições/trabalhos em voo e verifica todos os
consumidores. `IOptions` em singleton não comprova recarga dinâmica; processo antigo pode continuar operando.
Só então obtém o inventário final agregado pelo script
[`regional_readiness.sql`](../../scripts/geolocation/regional_readiness.sql), com transação REPEATABLE READ
READ ONLY, lock_timeout2s e statement_timeout10s. Não há consulta a dados individuais nem escrita.
Se faltar schema/privilégio ou houver timeout, interromper e analisar; não repetir indefinidamente ou ampliar
privilégios. O catálogo retornado não comprova países habilitados operacionalmente.

Registrar rodadas por estado/algoritmo, V2 abertas ou futuras, jobs/leases e inconsistências de versão.
V2 futura deve terminar ou passar por cancelamento/republicação administrativa revisada. Não reclassificar
histórico por SQL. Definir UTC/offset de corte somente após aceite explícito do inventário e da comunicação,
servidor/Worker/SPA compatíveis e capacidade do alvo. Conservar preferências explícitas e pending sem backfill.
Com corte ativo, o aviso da SPA deve refletir o filtro; texto anterior de filtro ainda inativo não serve.
Binário antigo, lease ativo, inconsistência ou configuração não comprovada impedem a retomada.

## G5 — ensaio posterior separado

Preparar um ambiente/caminho/bucket/prefixo de ensaio autorizados, sem corromper a base servida ou acionar
participantes. Reutilizar os testes existentes de fencing, idade, retenção, restart e entrega incerta.
Executar apenas a lacuna real: cópia e exclusão no provedor dedicado; promoção/refresh e restauração de uma
base admissível sem rejuvenescimento; uma ocorrência e uma recuperação correlacionadas até o Gmail designado.
O plano do ensaio deve indicar isolamento, limites, efeitos externos e como preservar o estado anterior.
Não usar ausência de mensagem como prova de recusa SMTP; respeitar o journal e reconciliar o mesmo Message-ID
antes de qualquer nova tentativa. Sem rollback destrutivo, base expirada ou extensão artificial de validade.

## Saída e critérios de interrupção

Entregar um recibo por gate com instante UTC, identidade do código/consumidor, autorização/passage, limites,
fontes reutilizadas, resultado observado e lacunas. Comparar identidades/mounts/proteções antes/depois e
devolver a janela. Não registrar G2/G3/G4/G5 como aceitos sem seus resultados e revisão correspondente.
G2 inventory sozinho não valida o hash; runner X64/JIT não prova VPS, clock independente ou precisão física.
Falha de integridade, drift de código/config, recurso insuficiente ou efeito fora do escopo interrompem
a operação; correção segue revisão e nova passagem. #108/#78/#79/#80 e #174 conservam seus critérios próprios.
