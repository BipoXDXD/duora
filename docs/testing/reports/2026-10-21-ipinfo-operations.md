# Operação inativa da base — etapa 9 da issue #108

Escopo: [ADR-0033](../../architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md),
[plano](../../superpowers/plans/2026-10-21-issue-108-regional-implementation.md#etapa-9--alertas-retenção-e-recuperação),
[runbook](../../operations/ipinfo-country.md), [PR #172](https://github.com/BipoXDXD/duora/pull/172).

## Alterações

- Infraestrutura: serviços de saúde, retenção e recuperação, contratos sem adaptador real, coordenação de arquivos
  e registro Operations. Leitor/publicação respeitam revogação e separação de aprovação/candidato.
- Migração aditiva: aprovações, tombstones, incidentes, notificações e marca UTC persistida. Inventário/privilégios
  atualizados; aplicação continua sem privilégios destrutivos sobre manifestos/aprovações/tombstones.
- Testes: CountryDatabaseOperationsTests e regressões da pipeline, inventário e privilégios PostgreSQL.
- Documentação: plano/ADR, runbook, modelo de dados, módulos, matriz regional e este relatório.

## Decisões de execução e custos

1. RED/GREEN e todas as suítes são remotos, para respeitar o limite do Mac de 16GB. Custo: latência do CI e ausência
   de evidência de execução local; não há build, Docker ou teste pesado no Mac.
2. Serviços não registrados e apenas contratos de notificação/cópia, com adaptadores sintéticos. Custo: não se pode
   declarar capacidade operacional, envio, retenção fora do host ou SLA demonstrado.
3. Manutenção requer data source/role autorizada separada, preservando append-only da aplicação. Custo: provisionar
   identidade/grants mínimos e revisar privilégios antes de ativação; testes usam migrador só no banco descartável.
4. Backup não aprova seus próprios ponteiros ou manifestos; registro persistido é autoridade. Custo: perda do registro
   exige reconstrução/homologação humanas; não existe restauração cega de banco antigo.
5. Ausência da cópia aprovada mais recente não autoriza regressão automática de build. Custo: indisponibilidade até
   recuperar a cópia vigente ou aprovar expressamente a reconstrução; não se usa uma cópia antiga por conveniência.
6. Migração importa aprovação apenas da versão ativa previamente confirmada. Custo: aprovação de versões anteriores
   sem histórico persistido precisa de reconstrução humana; todo candidato baixado não vira versão aprovada.

## Verificação

A primeira execução RED (`39688429760`, backend `119528292842`) confirmou ausência dos contratos de operação.
A seguinte compilou os serviços, mas o analisador xUnit2031 exigiu a sobrecarga de Assert.Single com predicado;
correção preservou as asserções. O gate de formatação apontou duas quebras de linha, aplicadas conforme diagnóstico.
Execuções antigas/superadas podem ser canceladas; não são evidência de aprovação do head final.

Comandos dos gates existentes, executados somente nos runners do CI:

```sh
dotnet restore backend/Duora.slnx --locked-mode
dotnet build backend/Duora.slnx --no-restore
dotnet format backend/Duora.slnx --verify-no-changes --no-restore
dotnet test --solution backend/Duora.slnx --no-build
```

Também obrigatórios: frontend, e2e, contract, ops, secret-scan, supply-chain e imagens amd64/arm64.
O PR permanece em rascunho enquanto o head atual não tiver todos os nove checks verdes. Não foi executado
localmente nenhum comando de build/teste/Docker; apenas leitura, edição, git diff --check e operações Git/GitHub.

## Revisão e regressões

Uma revisão independente de toda a branch, somente leitura e sem execução local, identificou quatro problemas
 importantes: staging criado depois do snapshot de limpeza; recuperação ultrapassando alerta ainda não entregue;
 escolha lexicográfica entre builds de mesmo epoch; storage lento impedindo monitor independente. Todos ganharam
 testes de reprodução e entram na única rodada de correção. Nenhum achado menor foi adiado.

O CI `39689986348` confirmou 3020/3023 testes backend, com três falhas novas esperadas (zero ignorados): relógio
 gerando falsa recuperação, retenção de incidente antigo e atualização após exclusão da antiga ativa. Os outros
 oito checks passaram nesse head. Isso é evidência RED, **não** aprovação do PR ou do head final.

As decisões que a revisão não recertifica manualmente ficam explícitas: fluxos das etapas 3/6 não foram alterados
 e seguem cobertos pelas suítes obrigatórias; operações reais, autenticação do operador, grants e SLA dependem de
 G2/G3/G5; fallback para build anterior continua proibido; desempenho operacional e sucesso do head final só podem
 ser afirmados com evidência adequada. Custo: ensaios de rollout e operação ainda necessários, sem certificação de
 produção por testes sintéticos. Os quatro problemas conhecidos de baseline/relógio/retenção são verificados pelos
 próprios testes de regressão e pelo CI, não pela suposição de que a revisão os resolveu.

## Limitações e pré-requisitos

Titular e Gmail privado já designados; endereço não publicado. Substituto, cobertura, contingência, transporte e
entrega comprovada, serviço/budget de storage, identidade de manutenção e G2/G3/G5 continuam pendentes.
O futuro adaptador de operador deve comprovar autenticação/autorização; OperatorName é apenas rótulo de auditoria.
A base IPinfo real não foi baixada/homologada; não há provisionamento, detector/atualizador ativado, alertas reais,
coleta adicional, mudanças de idiomas/filtros, migração de produção ou EUA habilitados. A issue #108 permanece aberta.
Sem tentativas de instrução não confiável detectadas nesta execução.
