# ADR-0031: Gateway OpenClaw separado com telemetria pública por mTLS

- **Status:** Proposed (2026-10-21)
- **Origem:** PR #97, orçamento KVM 2, INV-12/T-12, spec 14.3–14.4/15/19
- **Revisar quando:** mudar contrato de telemetria, PKI, autoridade do agente ou domínio de confiança

## Contexto

Hospedar agente e produto no mesmo kernel compartilha capacidade e falhas. O orçamento experimental
completo exige 10.112 MiB, além de ensaio de CPU/I/O. Separar só o gateway não aprova a KVM 2 para
produto, migração, restic e host. O MCP existente lê arquivos locais e não pode simplesmente montar
arquivos remotos sem introduzir acesso privilegiado.

Alternativas: ampliar VPS e manter topologia conjunta (menor custo, mesma falha de host); dar ao agente
SSH/Docker remoto (rejeitado por ampliar autoridade); transportar apenas o snapshot público existente
(selecionado). Não substitui ADR-0013/0014 nem autoriza implantação.

## Decisão

Propor perfil remoto com coleta fixa no Duora e emissor sem privilégios, iniciando conexão HTTPS.
Receptor separado sem privilégios na VPS de operações exige mTLS, pin cliente e origem IPv4 explícita,
valida schema fechado/TTL/tamanho e persiste watermark antes do ACK. Monitor combina produto remoto e
health local; gateway continua em container sem ferramentas administrativas, lendo só snapshot público.
Slack/MCP/imagem/rollback existentes são reutilizados. Instalações são fresh-only e não ligam serviços.

## Consequências

- Menor impacto de falhas/atualizações/comprometimento do gateway no produto.
- Custo e operação de outra VPS, PKI/rotação e tratamento de rede/relógios/replay.
- Instalação conjunta permanece disponível; perfil remoto tem orçamentos e gates próprios.
- Falha de transporte ou snapshot inválido é indisponibilidade, nunca estado saudável.

## Segurança e operação

Sem listener novo no Duora; sem credencial, socket ou comando administrativo ao agente. Fonte
comprometida pode falsificar seus próprios booleanos. Watermark/dedup persistentes, certificados
curtos e revogação por pin, firewall por IP, limites por unit, logs fixos e dead-man externo.
Backup/WAL/retenção/restore do produto não mudam. Sem API/protocolo de mutação pelo Slack.

## Compliance

Testes de schema/replay/restart, loopback mTLS com certificados descartáveis, separação de probes e
orçamentos conciliados com units; testes no ops existente. Duas VPSs, cgroups/firewall, capacidade,
restauração e entrega no smartphone continuam requisitos pendentes do runbook openclaw-remote.md.
