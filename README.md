# Duora

Duora organiza encontros entre duas pessoas adultas em eventos com rodadas curtas. Em vez de perfis num
catálogo, cada par joga uma experiência cooperativa (um minijogo de alguns minutos) e, ao final, cada pessoa
decide em privado se quer continuar a conversa. A conversa persistente só existe quando o interesse é mútuo.

Segurança faz parte do fluxo principal: sair de uma sala é imediato, bloqueio vale para pareamento, mensagens e
leituras, denúncias geram protocolo e recurso, e a participação depende de verificação etária. A maior parte
do trabalho de engenharia está em manter essas garantias sob concorrência, retries, reconexões, reinícios e
restauração de backup.

É um projeto de portfólio, desenvolvido por uma pessoa com apoio de agentes de código sob regras explícitas
([AGENTS.md](AGENTS.md)). A [especificação](docs/product/spec.md) descreve requisitos, decisões e o plano de
execução.

## Stack

| Camada | Tecnologia |
|---|---|
| Backend | C# 14, .NET 10 LTS, ASP.NET Core 10 (Minimal APIs, Identity, SignalR), EF Core 10 + Npgsql |
| Banco | PostgreSQL 18 |
| Frontend | React 19, TypeScript `strict`, Vite, Tailwind CSS 4, shadcn/ui, TanStack Query, React Router |
| Testes | xUnit v3, Testcontainers, `WebApplicationFactory`, Vitest, Testing Library, Playwright + axe |
| Operação | VPS Ubuntu Server 24.04 LTS, Docker Compose, Caddy, pgBackRest, OpenTelemetry |

Arquitetura: monólito modular com dois processos (web e worker) da mesma release, um banco e outbox
transacional ([ADR-0003](docs/architecture/adr/0003-monolito-modular-web-worker.md)).

## Estrutura do repositório

```text
.
├── AGENTS.md                # contrato de trabalho para agentes
├── global.json              # SDK .NET fixado
├── Directory.*.props        # build e versões centralizadas
├── backend/
│   ├── Duora.slnx
│   ├── src/                 # Core, Contracts, Infrastructure, Web, Worker
│   └── tests/               # UnitTests, ArchitectureTests, IntegrationTests
├── frontend/                # SPA React + TypeScript
├── infra/                   # compose/ (hoje), caddy, images, ansible, opentofu, runbooks (a criar)
├── scripts/                 # comandos reprodutíveis (doctor.sh)
└── docs/                    # produto, arquitetura, testes, automação, referências
```

A árvore completa prevista está na spec 21.1; diretórios entram conforme os marcos.

## Status

**M0 — Fundação verificável, em andamento.** Existem a solução .NET com os cinco projetos, testes de
arquitetura e de integração com PostgreSQL real, o ambiente local em Compose, o esqueleto do frontend e a
documentação base. Ainda faltam CI, contratos iniciais, infraestrutura de homologação e o ensaio sintético de
deploy e restore. Nenhuma funcionalidade de produto está implementada; todos os testes de aceite T-01 a T-28
estão como "não implementado" ([acceptance.md](docs/testing/acceptance.md)).

| Marco | Entrega |
|---|---|
| M0 | Fundação: build reproduzível no Mac e no Linux, CI, PostgreSQL real, ADRs |
| M1 | Identidade e proteção: cadastro, sessão, perfil, MFA administrativo, bloqueio, denúncia, fluxo etário |
| M2 | Fatia vertical: evento, rodada, pareamento, primeira experiência, chat de sala e decisão |
| M3 | Piloto operável: chat persistente, segunda experiência, administração, exportação/exclusão, alertas |

## Como rodar

Pré-requisitos e comandos: [docs/development/environment.md](docs/development/environment.md). Resumo:

```sh
scripts/doctor.sh                          # confere o ambiente
cd backend && dotnet test --solution Duora.slnx
```

## Documentação

| Documento | Conteúdo |
|---|---|
| [Especificação](docs/product/spec.md) | Requisitos, decisões e plano completos |
| [Escopo](docs/product/scope.md) | Escopo, invariantes, papéis, estados, parâmetros e glossário |
| [Jornadas](docs/product/journeys.md) | Fluxos, falhas e saídas obrigatórias |
| [Contexto](docs/architecture/context.md) | Containers e fronteiras de confiança |
| [Módulos](docs/architecture/modules.md) | Propriedade de dados e regras de dependência |
| [ADRs](docs/architecture/adr/README.md) | Decisões de arquitetura |
| [Testes de aceite](docs/testing/acceptance.md) | Matriz T-01 a T-28 |
| [Permissões da automação](docs/automation/permissions.md) | O que agentes e CI podem fazer |
| [Fluxo de trabalho](docs/automation/workflow.md) | Etapas, Definition of Ready e Done |
| [Fontes técnicas](docs/references/sources.yml) | Catálogo das 78 fichas de documentação |
| [Ambiente](docs/development/environment.md) | Setup local e diagnóstico |
