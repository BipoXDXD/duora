# Plano de homologação do gateway em VPS separada
Data: 21/10/2026
Estado: ensaios locais executados; instalação, contratação e ativação remota continuam pendentes. Resultados e limites em [validação local](telemetry-local-validation.md).

## 1. OBJETIVO E BASE

Demonstrar que o gateway pode operar em outra VPS sem comprometer PostgreSQL, web, workers, WAL, backups ou recuperação do Duora; que somente a fonte autorizada publica estados sanitizados; e que falhas chegam ao operador no smartphone sem depender da interpretação da processamento externo.

Base: PR113 integrado no commit60142612d772ba0bccbb7d488d1e8337b8c2f745; ADR0031; runbooks telemetry-remote.md, telemetry.md e capacity.md. Integração conferida no snapshot d79321d477362e3d207680bb68b378278febc21b: não há divergência nesses scripts, configurações e documentos em relação ao merge113. Selecionar para execução uma release/SHA completo e os digests aprovados, iguais nos dois lados. Não executar a partir de uma main móvel. Alterações posteriores exigem revisar o delta e repetir os checks afetados.

Fontes:
https://github.com/BipoXDXD/duora/blob/60142612d772ba0bccbb7d488d1e8337b8c2f745/docs/operations/telemetry-remote.md
https://github.com/BipoXDXD/duora/blob/60142612d772ba0bccbb7d488d1e8337b8c2f745/docs/operations/telemetry.md
https://github.com/BipoXDXD/duora/blob/60142612d772ba0bccbb7d488d1e8337b8c2f745/docs/operations/capacity.md

A homologação deve ocorrer primeiro em DUAS VPSs de teste, com configuração equivalente à pretendida e dados sintéticos. O gerador de carga fica em uma estação externa às duas VPSs. Reinícios, perda de rede, falhas de processo, snapshots corrompidos e testes de recuperação deste plano pertencem ao ambiente isolado. Testes de produção, mesmo somente carga ou backup manual, exigem outra autorização e janela.

Responsabilidades: operador de infraestrutura inventaria/configura/coleta evidências; responsável técnico decide aprovação; operador de PKI guarda CA e controla expiração/rotação; operador de alertas valida Slack/dead-man e o recebimento no telefone. Papéis podem ser da mesma pessoa, mantendo ações e credenciais separadas.

## 2. PRÉ-REQUISITOS DOS DOIS HOSTS

Host Duora de homologação — perfil source:
- MemTotal >=8.512 MiB, sem contar swap. Piso: produto5248 + migrador512 + restic1024 + coletor128 + emissor64 + margemhost1536 MiB.
- MemAvailable >=1.536 MiB antes da instalação/partida e nos preflights aplicáveis. Esse valor é uma fotografia; capacidade sustentada será ensaiada.
- A KVM2 de8GB decimais continua reprovada pelo orçamento. KVM4/4vCPU/16GB é candidata à medição, não aprovação automática.
- Ubuntu24.04, Python3.12/OpenSSL, systemd/cgroupv2, Docker e comandos de coleta existentes; relógio sincronizado e IPv4 de saída estável.
- Aplicação e PostgreSQL com release/digests fixados, limites existentes preservados, dataset sintético declarado e baseline funcional.
- Slice duora-media.slice carregada com MemoryMax=1GiB para a SOMA das units restic; medir o dataset realista do ensaio. WAL, timers, retenção, Object Lock e gates de recuperação preservados.
- Espaço medido para PostgreSQL/WAL, fotos, backups temporários, cache e logs; projeção de crescimento durante todo o ensaio e disco abaixo do limiar de interrupção.
- Nenhum listener de telemetria no host do produto. Somente saída HTTPS para o endpoint específico da VPS de operações.
- Instalação fresh-only: diretórios de integrações e conta duora-shipper ausentes; se já existirem, inventariar e usar plano de migração próprio, sem apagar ou sobrepor arquivos.

Host de operações de homologação — perfil remote-gateway:
- MemTotal >=3.456 MiB: gateway1536 + receptor128 + monitor128 + coletorlocal128 + margemhost1536 MiB. MemAvailable >=1.536 MiB nos gates.
- KVM1/1vCPU/4GB é candidata; tetos de CPU podem somar1,1vCPU, portanto testar contenção e throttling.
- Mesmo SO/runtime básico; Docker Composev2, backend iptables compatível com DOCKER-USER, IPv6 do container desativado e subnet172.30.40.0/24 sem colisão.
- Nenhum container Duora de produto, inclusive parado; não renomear o projeto para contornar o gate. Sem processamento externo local, browser ou builds.
- UID22001 e contas reservadas livres. Estado em filesystem próprio de2GiB. Mais de4GiB livres em /var/lib antes dessa reserva é requisito do instalador; comprovar espaço adicional após a reserva para imagem, logs e snapshots de atualização/rollback.
- IPv4 de bind específico e hostname/SAN do receptor definidos. Porta8443, ou outra não privilegiada revisada, permitida SOMENTE do IPv4 de saída do Duora no firewall do host e provedor. Sem wildcard, CDN/reverse proxy ou confiança em X-Forwarded-For.
- Gateway segue sem portas públicas; seu firewall bloqueia acesso ao host/redes privadas/metadata. Administração humana por canal separado, nunca pela processamento externo.
- Instalação fresh-only: ausência dos diretórios e contas reservadas descritos no runbook. Perfil root0644 com conteúdo exato remote-gateway.

Evidências de inventário de ambos: identificador não sensível do host, arquitetura, versões, MemTotal/MemAvailable, CPU/steal, cgroups, quotas/pesos, disco e espaço projetado, IPs/rotas/DNS e regras dedicadas. Inspecionar apenas campos necessários do Docker; nunca exportar inspect integral, Config.Env ou segredos.

## 3. PKI, IMAGEM E INTEGRAÇÕES

PKI/telemetria:
- CA privada dedicada e offline, distinta da CA de backup. Sua chave privada não vai para nenhuma VPS.
- Certificado servidor com SAN do hostname do endpoint e EKUserverAuth; certificado do emissor com EKUclientAuth. TLS>=1.2, CA/hostname verificados e fingerprint SHA256 DER do cliente permitido. Até dois pins apenas durante rotação.
- Definir validade curta (exemplo do runbook:30dias), responsável pela expiração e procedimento manual de revogação/rotação. Não há renovação ou CRL automática neste perfil.
- Arquivos de transporte em /etc/duora-telemetry/secrets: diretório root0700, arquivos root0400, disponibilizados exclusivamente por LoadCredential às respectivas units. Nunca montar essas credenciais no gateway.
- Endpoint, bind, port, source_ipv4 e pins revisados. Sem HTTP, redirect, proxy de ambiente ou verificação TLS desativada. NTP consistente: timestamps futuros são rejeitados.

Imagem/configuração gateway:
- Build em workstation/runner isolado, com SHA completo revisado e plugin Slack necessário; nunca build na VPS.
- Digest final, arquitetura, origem, assinatura/proveniência, SBOM, licenças e scanner HIGH/CRITICAL aprovados.
- Configuração do modelo e API própria, sem pressupor cobertura pela assinatura de chat.
- Executar futuramente config validate, security audit --deep, health e probe do catálogo MCP na imagem aprovada. Catálogo somente com as quatro ferramentas de leitura/política existentes, sem exec/fs/browser/administração.

Slack/GitHub/alertas — somente na VPS de operações:
- App Slack de teste dedicado, Socket Mode, app token connections:write, bot com escopos do manifest, canal privado e IDs explícitos de membros; webhook do monitor separado dos tokens do bot/modelo.
- Provisão de arquivos e permissões exatamente como telemetry.md. O gateway não recebe token GitHub, webhook ou certificados de transporte.
- Token GitHub fine-grained com Actions+Metadata somente leitura, apenas repositórios de teste listados (até10), com expiração/rotação. Um operador separado dispara as falhas/reexecuções de teste; não ampliar autoridade do monitor.
- Serviço de heartbeat externo à VPS de operações e verificação externa do Duora. Proposta de timeout:5min desde o último ping; recebimento por rota que continue funcionando se Slack falhar.
- Smartphone autenticado nos canais de teste, notificações permitidas e DND/economia de bateria revisados. Nomes/canais não autorizados não concedem autoridade.

## 4. ETAPAS E LIBERAÇÕES

Etapa0 — Preparação documental e baseline, antes de qualquer escrita nos hosts.
Entregar ficha de ambiente/release/digests, orçamento, PKI/expiração, regras de rede, dataset/carga, cenários, responsáveis, janela e rollback. Registrar estado inicial de produto/jobs/WAL e comprovar backup/restore sintético. Revisar checks do SHA escolhido; CI anterior não cobre automaticamente outro SHA.
Saída: pré-requisitos completos e aprovação registrada para instalar arquivos em homologação. Se faltarem hosts, capacidade, imagem, credenciais ou destino de alertas, manter a etapa pendente; não comprar recursos para desbloqueá-la automaticamente.

Etapa1 — Instalação de arquivos, após autorização futura.
Usar EXCLUSIVAMENTE os passos dos perfis source/gateway de telemetry-remote.md. Confirmar usuários, donos/modos, units/drop-ins e credenciais. Verificar que instalação não iniciou/habilitou serviço nem montou estado do gateway. Se houver instalação parcial, parar e inventariar antes de repetir.
Saída: revisão dos arquivos efetivos e do isolamento, gates de memória satisfeitos, nenhum segredo exposto e autorização separada para a partida de homologação.

Etapa2 — Partida controlada no ambiente de teste.
Seguir a ordem do runbook: montagem/ownership do estado em operações; receptor; coletor local/monitor; timer de envio no Duora; preflight e ativação da imagem aprovada do gateway. Tratar gateway inicialmente parado como estado esperado documentado, sem silenciar falhas do produto.
Saída: mTLS positivo, snapshot válido, produto e gateway saudáveis, Slack de teste responde ao usuário permitido, alerta determinístico recebido no telefone. Habilitar gateway no boot somente depois do aceite completo.

Etapa3 — Segurança, falhas e recuperação.
Executar a matriz abaixo com evidência por caso. Repor baseline entre falhas. Injetar corrupção/falha de disco somente no receptor de teste; nunca usar disco do banco ou backup real como alvo.
Saída: todos os casos obrigatórios aprovados, nenhuma falha classificada como saúde e recuperação comprovada.

Etapa4 — Carga, continuidade e repetição.
Executar os cenários de capacity.md, comparar baseline sem integração com integração completa e cumprir as metas abaixo. Repetir restart/reboot/rotação sob condições representativas. Proposta adicional:24h de observação contínua após a matriz, com ao menos um ciclo de backup/verificação; não substitui exercitar explicitamente jobs semanais.
Saída: relatório de capacidade/estabilidade/recuperação aprovado, sem efeitos no produto.

Etapa5 — Decisão humana.
Classificar cada caso como aprovado, reprovado ou pendente, com responsável e evidência. Aprovação autoriza somente elaborar a mudança de produção. Contratação, instalação ou ativação em produção exigem decisão própria sobre alvo, release/digest, janela, backup e rollback.

## 5. MATRIZ DE TESTES E CRITÉRIOS DE ACEITE

T01 — Runtime e isolamento.
Comprovar UID/GID, mounts read-only, capabilities removidas, no-new-privileges, PIDs, memória/swap/CPU e cgroups efetivos. Emissor e receptor não leem segredos do produto ou Docker socket; gateway não acessa host, redes privadas, metadata, banco, SSH ou PKI. Somente receptor na porta autorizada, além das portas administrativas já aprovadas. Negativas de rede devem falhar, e Slack/modelo devem continuar acessíveis conforme política.

T02 — Autenticação de transporte.
Envio do IP e certificado aprovados publica e recebe204. Certificado ausente, CA errada, pin não aprovado, expirado, hostname incorreto ou IP não permitido não publicam nem alteram watermark. Conferir também cert de mesma CA sem pin. Não aceitar fallback ou redirect.

T03 — Sanitização/framing.
Somente collected_at finito e conjunto exato telemetry.CHECKS com booleanos, corpo<=16KiB. Campos extras/ausentes, strings/logs/dados pessoais, JSON duplicado, NaN/bool/futuro/vencido, oversize, chunked, rota e framing incorretos são rejeitados sem alteração do snapshot. Headers/corpo lentos têm prazo absoluto de5s; não geram crescimento ilimitado. Nenhum payload sensível ou certificado privado nos logs.

T04 — Replay, ACK e disco.
Timestamp antigo ou igual com conteúdo diferente é rejeitado antes e depois de restart. Retry idêntico por perda de ACK recebe204 somente depois de confirmar persistência, sem atualizar collected_at/mtime ou estender validade. Repetir regressão de fsync após rename: falha de disco impede ACK até recuperação. Estado corrompido não é resetado nem aceito silenciosamente; recuperar conforme runbook, preservando watermark ou revogando certificado quando necessário.

T05 — Telemetria indisponível.
Parar coletor, interromper envio e rejeitar certificado. Snapshot com mais de120s falha na validação seguinte; publicação recente do monitor não renova o produto. Restaurar transporte e comprovar avanço com coleta nova, inclusive após interrupção longa. Registrar instante da última coleta, detecção, envio e recebimento no telefone.

T06 — Slack e proteção contra comandos.
Usuário/canal permitidos obtêm consulta com informação verificável. Outro usuário, canal, DM e bot não acionam consultas. Pedidos de sudo, deploy, restore, leitura de arquivos/segredos, inclusive em texto citado, não produzem execução, concessão de autorização ou leitura privilegiada. As quatro ferramentas MCP permanecem sem argumentos arbitrários.

T07 — Alertas independentes.
Parar gateway: consultas ficam indisponíveis e o monitor continua. Derrubar o Duora de teste: monitor remoto identifica perda e entrega alerta. Derrubar operações: heartbeat externo detecta ausência e chega no telefone. Simular Slack indisponível: entrega não é marcada como concluída; depois da recuperação há retry. Também comprovar rota externa independente do Slack. A ausência de heartbeat pode significar falha parcial; não classificar automaticamente como queda total.

T08 — GitHub Actions lento/indisponível.
Workflow sintético falho, reexecução, 403/revogação,429, timeout e paginação/saturação não são CI verde. Dedup por tentativa persiste em restart; coverage=false é tratado como falha de cobertura. Medir impacto na avaliação de telemetria/alertas: o polling atual é síncrono e pode atrasar o ciclo. Se ultrapassar as metas de alerta acordadas, reprovar e corrigir em outro PR; não esconder a latência nem ampliar a meta sem revisão.

T09 — Backup e indicadores.
Marcadores ausentes/vencidos e units em failed geram alerta. Executar full/diff e restic backup/prune/check/verify dentro dos limites e medir duração/WAL/spool. Marcador recente não comprova restore; processo worker running não comprova progresso dos jobs. Exigir evidência de filas/deadlines e recuperação efetiva.

T10 — Rotação e ciclo de vida.
Rotacionar cliente com dois pins temporários, retirar o antigo e comprovar rejeição; rotacionar CA/servidor em transição controlada. Restart e reboot preservam watermark/dedup, montagem, ownership, timer, monitor e firewall. Atualização com imagem sem saúde/digest inválido é rejeitada e restaura imagem/estado anteriores do gateway, sem alterar produto.

T11 — Restore/reconciliação.
Restaurar banco e mídia do ensaio em destino isolado; aplicação/postgres/worker seguem a sequência do runbook de recuperação, sem perfil restore concorrente de produção. Comprovar integridade, reconciliação de exclusões/sessões, ausência de efeitos externos duplicados e metas de RPO/RTO. Não restaurar produto para reverter apenas gateway.

Metas adicionais de alerta propostas para aprovação ANTES do ensaio:
- Com Slack/rede saudáveis: alerta de freshness recebido até180s após a última coleta; recuperação recebida até60s após o monitor observar uma coleta válida. São metas de homologação, não garantias já demonstradas pelo código.
- Dead-man: ausência detectada após5min desde o último ping; proposta de chegada no smartphone até6min. Registrar separadamente timeout configurado e atraso de entrega do provedor.
- Durante falha do Slack, exigir a rota externa e o envio pendente após recuperação; não cobrar entrega por Slack enquanto ele estiver indisponível.
- Falha de API/paginação do GitHub não pode tornar aceitável perda de cobertura de alerta. TTL120s limita a validade do dado, não garante sozinho latência de notificação.

## 6. CARGA E CRITÉRIOS DE CAPACIDADE

Seguir integralmente os cenários de capacity.md, com3 repetições e5min de aquecimento por cenário. Proposta inicial de dataset:1000 contas sintéticas e100000 mensagens, com cardinalidade/volume de mídia declarados. Gerador fora das VPSs. Modelo/Slack só recebem estados e mensagens operacionais sintéticas; nenhum participante ou destinatário real.

- Rampa10/25/50/100 participantes,5min por degrau, seguida de100 por30min, até50 salas. É alvo de teste, não lotação aprovada.
- Entrada100 em60s e reconexão de50% em10s,3 repetições.
- Repetir referência com WAL contínuo, full e depois diff; restic e processamento de imagens concorrentes conforme os limites existentes. Concorrência de imagens permanece1. Rodar até completar backup e pelo menos a duração prevista do cenário.
- Incluir migração controlada e atualização do gateway sem duas instâncias simultâneas; não fazer restore concorrente da aplicação. Falhas/recuperação de web/worker e retorno ao baseline são ensaiados isoladamente.
- Amostrar a cada5s CPU/steal/iowait/throttling, PSI, MemAvailable, uso/pico/OOM por cgroup, swap, disco/fila; aplicação p50/p95/p99, erros, heap/GC/pools, locks, jobs/outbox/deadlines; WAL/archive/spool e duração dos backups.

Critérios existentes do runbook para adotar no ensaio:
- Leituras p95<=250ms; comandos p95<=300ms; reconciliação p95<=1s.
- 5xx+timeout/transporte<1%, com RTT cliente separado da latência da aplicação.
- Nenhuma violação de invariantes, OOM, restart inesperado, deadline perdido ou fila crescendo sem drenagem; voltar ao baseline após15min de recuperação.
- Restore: RPO observado<=5min e RTO do banco<=2h, metas propostas a comprovar; medir também recuperação de mídia e tempo operacional total, sem inferir a mesma meta para estes.
- Meta de outbox: investigar idade>30s por5min e verificar todos os deadlines críticos.

Interromper escalada/carga e preservar evidências se MemAvailable<512MiB por60s, swapping contínuo, disco>=85%, archive atrasado>=4min, OOM, erro inesperado de backup ou falha funcional não induzida. CPU>85% por5min ou throttling em>10% dos períodos exige investigação e repetição, não reprovação automática isolada. Falhas deliberadamente induzidas só no ambiente/componente previstos têm resultado avaliado pelo caso específico; não justificam impactar o produto.

Não reduzir retenção, Object Lock, verificações, segurança, margem ou testes para obter aprovação. Se restic exceder1GiB, revisar orçamento e repetir o ensaio antes de promover.

## 7. EVIDÊNCIAS, ROLLBACK E DECISÃO

Ficha por caso: ID, responsável, host/papel, data/fuso, SHA/digests, configuração e dataset, comando/ação, início/fim, resultado esperado/observado, latências, métricas, registro sanitizado, aprovado/reprovado/pendente e ação corretiva. Anexar evidências do smartphone sem dados pessoais/URLs secretas. Nunca incluir tokens, certificados privados, ambientes completos, fotos, e-mails ou conversas reais.

Rollback: parar/desabilitar exclusivamente timer/serviço de envio no Duora e receptor/monitor/gateway/coletor local em operações; revogar cliente/tokens e fechar somente ingresso dedicado, preservando watermark, dedup, estado e evidências. Manter WAL, PostgreSQL, web, workers, backups, volumes e regras de recuperação do produto. Para rollback só da imagem do gateway, usar activate.py e captura correspondente de imagem/config/state. Não apagar watermark para contornar falha; recuperação sem watermark confiável exige novo certificado/pin e janela auditada.

APROVADO PARA PLANEJAR PRODUÇÃO somente se:
- Pré-requisitos de ambos os hosts comprovados e todos os casos obrigatórios aprovados.
- Limites e isolamento efetivos, carga/deadlines e backup/restore aceitos.
- Alertas de falha/recuperação, queda dos hosts e indisponibilidade Slack/GitHub comprovados no telefone dentro das metas acordadas.
- Rotação, expiração, reboot/update/rollback e recuperação do watermark documentados e ensaiados.
- Nenhuma falha crítica pendente, lacuna de cobertura mascarada ou segredo exposto.
- Relatório assinado pelo responsável técnico com as limitações e capacidade efetivamente aprovadas.

Faltam, para executar os ensaios nas VPSs: inventários/autorização dos ambientes de homologação, imagem/digest aprovado, PKI e credenciais privadas, configuração dos canais externos e janela/responsáveis. O CI do PR113 é evidência de código; não comprova esses itens. Os resultados locais estão no relatório vinculado acima; contratação, acesso às VPSs, instalação e ativação não foram realizados.
