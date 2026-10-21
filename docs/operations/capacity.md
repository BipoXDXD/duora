# Orçamento e validação da Hostinger KVM 2

Referências: issue #86, spec 15.1/19.1/20.4, T-24 (carga), T-08 (reinício), T-11/T-25
(recuperação), INV-07/08/11/12. Nenhum contrato API ou regra de autorização muda.

## Orçamento experimental

Máquina informada pelo responsável: **2 vCPU e 8 GB**, Ubuntu Server 24.04 LTS, Boston.
Não há medição de capacidade neste documento. Confirmar `MemTotal`, arquitetura, kernel, versão do
Docker/Compose, cgroup, CPU steal e armazenamento no inventário do ensaio. O cálculo estático usa
8.000.000.000 bytes (7.629 MiB arredondados para baixo), sem pressupor 8 GiB utilizáveis.

| Serviço | Teto CPU (vCPU) | Peso CPU | Teto RAM (MiB) | Papel |
|---|---:|---:|---:|---|
| PostgreSQL | 1,5 | 2048 | 2048 | Transações, locks, jobs e WAL |
| Web ASP.NET Core | 1,5 | 2048 | 1536 | API e SignalR |
| Worker | 1 | 768 | 1024 | Jobs com prazo, outbox, imagens |
| Backup | 0,5 | 512 | 512 | Recepção contínua de WAL e backup físico |
| Caddy/edge | 0,5 | 2048 | 128 | TLS, proxy e SPA |
| Migrador (perfil migrate) | 1 | 512 | 512 | Job transitório do deploy |
| Restore (perfil restore) | 2 | 512 | 1024 | Recuperação com aplicação e PostgreSQL parados |

Tetos permanentes: **5 vCPU e 5.248 MiB**. CPU é um máximo por container, não reserva, mínimo,
partição fixa ou soma exigida do host. `cpu_shares` é peso relativo entre processos executáveis sob
contenção, sujeito ao scheduler/cgroup; não é porcentagem garantida. PostgreSQL/web podem aproveitar
CPU ociosa. O teto de backup de 0,5 reduz competição por CPU; não limita I/O e pode alongar o backup
ou atrasar WAL. O servidor TLS e os comandos `docker exec` compartilham esse mesmo orçamento.
Manter o WAL contínuo; agendar full/diff e retenção fora do pico, sem desligar archive para ganhar CPU.
O worker não recebe a menor prioridade porque prazos e revogações dependem dele. Concorrência de
imagens continua 1; não aumentar concorrência para compensar fila sem medir CPU, memória e deadlines.

Memória: 5.248 MiB permanentes + 512 MiB do migrador + 1.536 MiB de margem do host = **7.296 MiB**
(7.650.312.192 bytes). O teste conta o worker durante migração por conservadorismo, embora o deploy
atual o pare. Restore não é simultâneo à aplicação/migração: parar web/worker/postgres; backup e edge
mais restore somam 1.664 MiB. Proibido usar o perfil restore como carga concorrente de produção.
Sem blue-green ou réplicas extras (ADR-0009). A margem de host é planejamento, não reserva Docker:
cobre SO, daemon, agentes e memória não cobrada nos containers; confirmar folga real por `MemAvailable`.
Caches, `tmpfs` e shared memory cobrados no cgroup consomem o teto do container. Swap não amplia o orçamento.
Este subtotal cobre somente o Compose de produto. Observadores e restic do host têm orçamento
separado abaixo; não usar a folga deste subtotal como autorização para ativá-los.

PostgreSQL mantém `shared_buffers=512MB` e `max_connections=100`. A redução do teto de 2,5 para 2 GiB
preserva folga para host/migração; é hipótese a validar. `work_mem` multiplica por operação/conexão e
paralelismo; registrar valores efetivos, pools web/worker, autovacuum e memória de queries no ensaio.
Não multiplicar conexões ou buffers só porque o limite do container parece suficiente. No .NET, medir
heap/GC, alocações, thread pool e working set, incluindo imagens; não fixar heap pelo teto total.

Semântica consultada: [Docker Compose services](https://docs.docker.com/reference/compose-file/services/)
(`cpu_shares`, `cpus`, `mem_limit`, `deploy`), em 2026-10-19. Validar a aplicação pelo runtime:
`docker compose config` sozinho não prova enforcement. Após autorização, registrar `HostConfig.NanoCpus`,
`CpuShares`, `Memory` e os valores efetivos de `cpu.max`, `cpu.weight`, `memory.max` dos cgroups.
Não copiar `docker inspect` integral nem ambientes/segredos para relatórios.

## Ensaio pendente

Executar primeiro em host isolado equivalente, com dados sintéticos e gerador de carga **fora** da VPS.
Medir na KVM 2 real somente em janela explicitamente autorizada; esta proposta não autoriza deploy,
restart, backup manual nem teste de carga na produção. Não usar dados de participantes.

Registrar commit, digests, parâmetros, runtime, `MemTotal`, disco/IOPS, RTT, região dos clientes,
seed/dataset, amostras, taxa oferecida/aceita, duração e rotas cobertas. Usar contas sintéticas com
histórico e dataset de tamanho declarado (proposta inicial: 1.000 contas e 100.000 mensagens), sem
fornecedores reais ou envio de e-mail a pessoas. Fluxos ainda não implementados ficam **pendentes**;
um stub rápido não comprova o fluxo real. Executar 3 repetições por cenário, com 5 min de aquecimento.

| Cenário | Carga e duração propostas | O que observar |
|---|---|---|
| Referência | Rampa de 10, 25, 50 e 100 participantes, 5 min por degrau; 100 por 30 min, até 50 salas | Login/check-in, leitura a cada 10 s, comando a cada 5 s e mensagem a cada 15 s por cliente; conexões SignalR ativas; transições de rodada |
| Entrada/reconexão | 100 entradas em 60 s; reconectar 50% dos clientes em 10 s, repetir 3 vezes | Hash de senha, pools, TLS, reconciliação após commit, fila de thread pool |
| Contenção | Repetir referência com full e depois diff, WAL contínuo, imagens no limite permitido (concorrência 1) e retenção em lotes; 30 min ou até backup terminar | CPU/throttling, I/O, archive, filas, memória do processamento e duração do backup |
| Recuperação de processos | No host isolado, reiniciar web e depois worker durante referência | Deadlines persistidos, replay/idempotência, lease/fencing, sem efeitos duplicados; T-08 |
| Saturação/recuperação | Acima de 100 somente no ensaio isolado: incrementos de 25 a cada 5 min até primeiro gate violado; voltar ao último degrau aprovado por 15 min | Limite de admissão e drenagem; não concluir que 100 já foram aprovados |
| Recuperação de dados | Restore isolado do backup do ensaio, sem aplicação concorrente | Integridade, reconciliação T-11, RPO observado ≤ 5 min e RTO do banco ≤ 2 h (metas propostas, não resultados) |

Métricas a cada 5 s: CPU por serviço e host, steal/iowait, throttling (`nr_throttled/nr_periods`
e tempo), pressão PSI CPU/memória/I/O, `MemAvailable`, memória atual/pico/OOM por cgroup, swap-in/out,
latência/fila/throughput de disco. Aplicação: p50/p95/p99 e contagem por operação, 5xx, timeouts, 4xx/429
separados, heap/pausas GC, thread-pool queue, conexões/espera de pool, queries/locks/deadlocks e autovacuum.
Jobs/outbox: idade mais antiga, tamanho, atraso de deadline, renovação de lease e drenagem. Backup:
WAL produzido/arquivado, último archive bem-sucedido, fila/spool, erro, duração e RPO de restore observado.
Não incluir payloads, tokens, e-mails ou identificadores pessoais na telemetria.

Critérios propostos de aprovação (não medições): leituras p95 ≤ 250 ms, comandos p95 ≤ 300 ms,
reconciliação p95 ≤ 1 s e falhas 5xx + timeout/transporte < 1% (spec 19.1), sem violação de invariantes,
OOM, restart inesperado ou deadline perdido. Separar latência da aplicação de RTT cliente (Brasil e
países do lançamento); reportar ambas. Nenhuma fila cresce indefinidamente ou permanece acima do baseline
após os 15 min de recuperação. Alerta de outbox: idade > 30 s por 5 min; verificar todo deadline crítico.
Interromper a escalada se `MemAvailable` < 512 MiB por 60 s, OOM, swapping contínuo, disco ≥ 85%,
archive atrasado ≥ 4 min (antes da meta de 5 min), erro de backup ou falha funcional. CPU > 85% por
5 min ou throttling em > 10% dos períodos é gatilho de investigação e repetição, não prova isolada de
falha. Correlacionar com PSI, filas e latência; registrar também o período/denominador da medição.

Se falhar, reduzir admissão/concorrência pesada, revisar queries/pools ou aumentar capacidade com evidência.
Não aumentar tetos acima da máquina nem reduzir garantias de backup/segurança para passar no ensaio.
Rollback de limites: release/Compose anterior aprovado, na janela operacional; sem migração de schema.
Só promover após revisão humana, gates de CI e ensaio aprovado; não há capacidade aprovada de 100 pessoas.

## Evidências e pendências

- Verificação estática: `python3 -m unittest discover -s scripts/deploy -p 'test_capacity_budget.py' -v`.
  Integrada pelo `discover` existente do job `ops`, sem alterar workflows ou gates.
- Esse teste cobre todos os serviços, perfis, teto individual ≤ 2 vCPU, memória individual e agregada,
  margem de host, prioridade e réplicas. Não soma CPU como reservas.
- Normalização Docker Compose, enforcement dos cgroups, cenários de carga e restauração na KVM 2:
  **pendentes**. Registrar resultados no PR/issue com comando, ambiente e saída; nenhum benchmark foi executado.
- Coordenação: branch baseada no PR #83. Integrar #83 primeiro, rebasear/redirecionar este PR para main
  e repetir checks. Ao integrar #84, preservar orçamento da spec 15.1/23.2; a lacuna histórica do ADR-0028
  aponta para #86, cujo fechamento depende das evidências/revisão. Mídia TURN externa não elimina o custo
  de sinalização/credenciais na API; ensaiar essas rotas quando implementadas.

Consolidação #93: tetos de CPU de PostgreSQL/web e peso do edge conciliados com a alternativa do #89; RAM do #87. OpenClaw/monitor permanecem desligados; antes de ativar, incluir seus containers em todos os cenários e medir a margem.


## Reconciliação OpenClaw e backup de mídia

A main após #96 contém dois Composes e serviços no host. O orçamento anterior de 7.296 MiB
não incluía OpenClaw, observadores nem o restic. Somar limites não prova consumo nem OOM,
mas invalida a aprovação estática da topologia completa na KVM 2 de 8 GB decimais.

| Cenário conservador | Tetos de processos (MiB) | Margem host (MiB) | Total (MiB) |
|---|---:|---:|---:|
| Produto + gateway + observadores | 7.040 | 1.536 | 8.576 |
| Acima + migrador, sem restic | 7.552 | 1.536 | 9.088 |
| Produto + gateway + observadores + restic | 8.064 | 1.536 | 9.600 |
| Acima + migrador (worker contado) | 8.576 | 1.536 | **10.112** |
| Restore, web/worker/postgres parados + gateway/observadores/restic | 4.480 | 1.536 | 6.016 |
| Sem gateway, com observadores/restic/migrador | 7.040 | 1.536 | 8.576 |

Full/diff/check físico executam no container backup (512 MiB já contados); comandos executados
no postgres compartilham o limite desse container. O lock existente serializa full/diff/verificação
do repositório com deploy. O cálculo ainda permite sobreposição conservadora. WAL não é suspenso.
Restic roda no host e pode coincidir com migração, banco e outras units de mídia; seu lock de
repositório não é limite de memória. A slice `duora-media.slice` limita **a soma** dessas units
a 1.024 MiB (MemoryHigh 768 MiB, sem swap adicional, CPUQuota 50%). Não é 1 GiB por instância.
Instalada pela role backup, preserva os timers, OnFailure, retenção, Object Lock e gates de restore.
Comandos administrativos diretos fora da slice não estão cobertos: executar operações manuais em
unit transitória na mesma slice, com comando revisado, ou em host isolado e janela exclusiva.
Não executar init/restore/prune manual concorrente fora desse orçamento.

Não há medição que aprove 1 GiB para o dataset real do restic. Ensaiar backup, prune, check e restore
com volume e cardinalidade representativos; pressão/OOM/falha exige redimensionar e recalcular,
jamais aceitar marcador antigo como sucesso, desativar verificações ou reduzir retenção.
A capacidade anterior do produto também precisa ser reavaliada com mídia: 8.320 MiB com migração,
restic e margem, mesmo sem qualquer observador. Não anunciar que desligar apenas o gateway resolve.

Preservados PostgreSQL 2.048, web 1.536, worker 1.024 e backup 512 MiB. Gateway mantém 1.536 MiB
(heap Node 1.024 MiB não cobre processos auxiliares, buffers ou tmpfs); CPU reduzida para 0,5,
peso 256 abaixo do backup 512, `oom_score_adj=500` favorece sacrificar o agente sob OOM global,
sem garantir imunidade do produto. Observadores mantêm 128 MiB/20% cada, sem swap adicional,
pesos CPU/I/O 25. Pesos de systemd e Docker dependem da hierarquia: verificar cgroups, não
comparar números entre branches como garantia de prioridade. IOWeight depende do controlador/disco.
CPU concorrente pode alcançar tetos de 6,4 vCPU (produto 5 + gateway 0,5 + observadores 0,4 +
restic 0,5), ou 7,4 com migrador, em host de 2 vCPU. Isso é oversubscription, não reserva;
latência, throttling, CPU steal e atraso de archive precisam passar no ensaio.

`capacity.py` recusa instalação antes de criar recursos se `MemTotal` < 10.112 MiB ou
`MemAvailable` < 1.536 MiB, sem usar swap como RAM. Preflight, ativação e partida systemd
repetem o gate; preflight também exige MemoryMax=1.024 MiB na slice de mídia carregada.
Os testes conciliam constantes com Compose/units, rejeitam inventário ausente/malformado e KVM 2.
Esse piso não garante desempenho, não lê consumo de produção e não verifica sozinho drift de
todos os limites instalados. Validar o Compose efetivo, todas as units/drop-ins e cgroups no ensaio.

Caminho proposto: aumentar a RAM utilizável acima do piso e ensaiar CPU/I/O; ou preparar uma
mudança separada para gateway remoto. O segundo caminho requer transporte autenticado da telemetria,
pois os bind mounts locais atuais não funcionam em outro host. Não expor Docker/SSH como atalho.
Não reduzir limites essenciais ou o gateway para caber por aritmética sem benchmark.

Executar os cenários anteriores com Slack/MCP, polling Actions, restic e full/diff concorrentes;
ensaiar atualização (gateway parado durante config validate, sem duas instâncias), migração,
reboot e rollback. Registrar memória atual/pico/OOM, CPU, PSI e I/O de todos os cgroups, incluindo
slice de mídia e processos auxiliares. Reservar espaço para imagens, 2 GiB de estado, snapshots
privados do agente, WAL e mídia; medir livre **depois** das reservas. Não construir imagens na VPS.
O `MemAvailable` do gate é só uma fotografia, não aprovação de carga sustentada.

Referências de semântica: [Compose services](https://docs.docker.com/reference/compose-file/services/)
e [systemd resource control](https://www.freedesktop.org/software/systemd/man/latest/systemd.resource-control.html).
Enforcement em Ubuntu 24.04/cgroup v2 e todos os cenários continuam pendentes de homologação.
