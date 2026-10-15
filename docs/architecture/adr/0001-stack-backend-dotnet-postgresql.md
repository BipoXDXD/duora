# ADR-0001: Backend em C#/.NET 10 LTS, ASP.NET Core, EF Core/Npgsql e PostgreSQL 18

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 2.1, 2.3 e 23.1 (ADR-001)
- **Revisar quando:** a política de suporte mudar ou um requisito demonstrado não couber na stack

## Contexto

O Duora precisa de API HTTP, autenticação com sessões revogáveis, tempo real, jobs duráveis e transações
que atravessam conta, relação, decisão e outbox. A stack foi definida como requisito do produto, e o projeto
é desenvolvido por uma pessoa com apoio de agentes: coesão entre runtime, framework web, ORM e banco reduz a
superfície a manter.

Alternativas registradas na spec:

- **Seguir sempre a major mais nova de cada componente.** Descartada: PostgreSQL 18 é escolha explícita, e
  upgrade de major é procedimento separado, ensaiado (spec 2.1, 15.4).
- **Versões preview da linha .NET.** Descartadas: a base usa só a linha estável 10.
- **Bibliotecas de conveniência por reflexo** (MediatR, AutoMapper, FluentAssertions, Hangfire, bibliotecas de
  imagem). Não entram sem avaliar necessidade, manutenção, compatibilidade e licença (spec 2.3). A base
  funciona com DI, handlers e mapeamentos explícitos.
- **Banco de testes substituto** (EF Core InMemory, SQLite). Descartado para tudo que depende de SQL, locks,
  tipos ou constraints (spec 20.1).

## Decisão

Vamos usar C# 14 sobre .NET 10 LTS (`net10.0`), ASP.NET Core 10 com Minimal APIs organizadas por
funcionalidade, EF Core 10 com Npgsql.EntityFrameworkCore.PostgreSQL 10 e PostgreSQL 18, com a mesma major em
desenvolvimento, CI e produção. LINQ atende as consultas usuais; SQL parametrizado atende locks e filas.

Porquê técnico: a linha 10 é LTS, os componentes evoluem em majors compatíveis e o PostgreSQL oferece as
garantias de que as invariantes dependem (constraints, índices parciais, `FOR UPDATE SKIP LOCKED`, WAL/PITR).
Porquê de negócio: uma stack coesa e suportada reduz o custo de manutenção de um projeto de uma pessoa.

## Consequências

- Positivas: um ecossistema só para web, worker e testes; Identity, SignalR e OpenAPI nativos; PostgreSQL real
  nos testes de integração via Testcontainers.
- Negativas: a linha precisa acompanhar patches de segurança, e o fim do suporte do .NET 10 (anunciado para
  novembro de 2028, a conferir na política oficial) força uma migração planejada.
- Trade-off: SQL direto e `ExecuteUpdate` não passam pelas regras de `SaveChanges`; toda escrita crítica
  continua obrigada a respeitar bloqueio, versão e outbox (spec 10.5).

## Segurança e operação

Patches de runtime, SDK e pacotes são revisados, não aplicados em silêncio. Pacotes vêm de fontes explícitas,
com lockfile, auditoria de vulnerabilidades e inventário de licenças (spec 2.3).

## Compliance

- `global.json` fixa o SDK; `Directory.Packages.props` centraliza versões; `packages.lock.json` versionados.
- CI roda `dotnet restore --locked-mode` e falha com lockfile inconsistente (spec 17.1; a implementar no
  workflow de M0).
- `Duora.ArchitectureTests` verifica que `Duora.Core` não referencia ASP.NET Core, EF Core nem Npgsql.
- Testes de integração sobem PostgreSQL 18 real; revisão rejeita InMemory/SQLite como substituto.
