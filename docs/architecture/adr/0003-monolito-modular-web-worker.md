# ADR-0003: Monólito modular com processos web e worker da mesma release

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 3.1–3.3 e 23.1 (ADR-003)
- **Revisar quando:** escala ou domínio exigirem autonomia real de deploy entre partes

## Contexto

Os casos de uso críticos (decisão mútua, bloqueio, reserva de sala) precisam de transação ACID que atravessa
vários módulos. Ao mesmo tempo, processamento de imagens e rotinas demoradas não podem disputar recursos com
HTTP e SignalR. A equipe é de uma pessoa, e todas as partes compartilham as mesmas características
operacionais no piloto.

Alternativas consideradas:

- **Microsserviços.** Descartados: quebrariam as transações das invariantes INV-02, INV-03 e INV-04 e
  exigiriam sagas, broker e observabilidade distribuída sem driver mensurável (ADR-0015).
- **Processo único.** Descartado: jobs de imagem e rotinas longas competiriam com o hub e a API.
- **Arquitetura cerimonial** (interface por classe, quatro arquivos por leitura simples). Descartada (spec 3.3).

## Decisão

Vamos manter um único modelo de domínio e um banco relacional, publicados como uma release com dois
processos: **web** (`Duora.Web`: HTTP, Identity, SignalR) e **worker** (`Duora.Worker`: jobs duráveis e
integrações). O código se organiza em `Duora.Core` (regras e casos de uso por módulo), `Duora.Infrastructure`
(EF Core e fornecedores) e `Duora.Contracts` (contratos de transporte). Web e Worker são composition roots.
Operações entre módulos usam coordenador de caso de uso, nunca HTTP interno. Só o processo web publica a outbox
de tempo real; o worker não usa `IHubContext` local.

Porquê técnico: atomicidade local para as invariantes e isolamento de recursos entre processos. Porquê de
negócio: operação simples e custo baixo para um piloto numa VPS.

## Consequências

- Positivas: transações locais, deploy único, testes de integração cobrindo o fluxo inteiro.
- Negativas: fronteiras de módulo dependem de disciplina e de testes de arquitetura, não de rede; web e worker
  precisam ser compatíveis com o mesmo schema a cada release.
- Trade-off: escalar uma parte isoladamente exige primeiro separar processo ou banco, com novo ADR.

## Segurança e operação

Worker sem endpoint público. Web e worker são testados e publicados juntos, com digests no manifesto da
release (spec 17.2).

## Compliance

- `Duora.ArchitectureTests`: Core sem ASP.NET Core/EF Core/Npgsql, grafo de projetos permitido e ausência de
  ciclos (existente desde M0). Regras de namespace por módulo: a implementar quando os módulos ganharem código
  (M1).
- Revisão de PR rejeita chamada HTTP interna entre módulos e uso de `IHubContext` no worker.
- Manifesto de release com digests de web e worker do mesmo commit (a implementar com o pipeline de release).
