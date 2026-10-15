# ADR-0015: Sem Kubernetes, Redis ou microsserviços obrigatórios

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 2.3, 15.1, 19.4 e 23.1 (ADR-015)
- **Revisar quando:** houver necessidade medida, não preferência de ferramenta

## Contexto

O piloto roda numa VPS Ubuntu Server 24.04 LTS com Docker Compose, operada por uma pessoa. Cada componente
adicional traz operação, segurança, backup e monitoramento próprios.

Alternativas registradas na spec (todas condicionais):

- **Kubernetes ou orquestrador.** Sem requisito que Compose numa VPS não atenda.
- **Redis/Valkey como cache ou estado compartilhado.** Só com ADR de consistência, operação e capacidade.
- **Microsserviços.** Ver ADR-0003.
- **Prometheus/Grafana/Loki/Tempo locais.** Só com orçamento de recursos separado; a base exporta para destino
  externo.

## Decisão

Vamos operar com Docker Engine + Compose numa VPS, PostgreSQL para dados, filas e outbox, cache local limitado
para conteúdo não sensível e Collector OpenTelemetry leve exportando para fora. Qualquer um desses componentes
só entra com necessidade medida e ADR próprio.

Porquê técnico: menos peças móveis numa máquina com orçamento de memória apertado (spec 15.1). Porquê de
negócio: custo e carga operacional compatíveis com um piloto de uma pessoa.

## Consequências

- Positivas: topologia que cabe num runbook; recuperação de VPS inteira ensaiável.
- Negativas: sem alta disponibilidade; containers na mesma máquina não são redundância.
- Trade-off: escalar exige primeiro medir e separar banco ou worker, com ADR (spec 19.4).

## Segurança e operação

Menos serviços expostos e menos segredos a custodiar. A indisponibilidade da VPS é risco assumido e monitorado
de fora (spec 19.1).

## Compliance

- Revisão de dependências: pacote ou imagem nova que introduza um desses componentes exige ADR (spec 14.2).
- Revisão dos arquivos de Compose de produção (a implementar em M0/M3).
