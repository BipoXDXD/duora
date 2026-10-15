# Fluxo de trabalho

Como uma mudança vai da especificação à produção. Fonte: spec 14.1, 21.3 e 22.1–22.2. Permissões de cada
etapa: [permissions.md](permissions.md). Regras para agentes: [`AGENTS.md`](../../AGENTS.md).

## Etapas (spec 14.1)

| Etapa | Entrada | Saída verificável | Limite |
|---|---|---|---|
| Especificação | Caso de uso, invariantes e riscos | Contrato, estados e critérios de aceite | Não inventar regra de segurança para preencher lacuna |
| Planejamento | Feature aprovada e dependências | Mudanças previstas, testes e impacto de migração | Escopo pequeno; mudança arquitetural exige ADR |
| Implementação | Branch isolada, dados sintéticos e documentação | Código, testes, migração e documentação da feature | Sem credenciais, SSH ou banco de produção |
| Revisão automática | Diff e resultados do CI | Achados com arquivo, motivo e reprodução | Resultado de modelo é sugestão, não autoridade |
| Validação independente | Suíte protegida e contratos | Evidência executada de comportamento e regressão | Não aceitar "testes passaram" sem logs e artefatos do runner |
| Promoção | Artefato imutável aprovado | Release em homologação e relatório de smoke tests | Mesmo digest que seguirá para produção |
| Operação | Runbook aprovado e escopo permitido | Ação registrada, métricas e resultado | Nada de shell genérico comandado por texto externo |

## Definition of Ready (spec 22.1)

Uma feature entra em implementação quando tem:

- [ ] objetivo delimitado e o que fica fora do escopo;
- [ ] `INV` e `API` afetados nomeados;
- [ ] contratos e estados (transições com precondições);
- [ ] autorização definida, incluindo os casos negativos;
- [ ] dados classificados (o que é privado, sensível ou público);
- [ ] cenários negativos e de concorrência;
- [ ] dependências conhecidas;
- [ ] critério de aceite ligado a testes (T-NN quando houver).

Lacuna funcional pode ser resolvida por proposta documentada. Lacuna de segurança não vira comportamento
permissivo: a feature espera a decisão.

## Definition of Done (spec 22.2)

Uma feature está pronta quando:

- [ ] código e migração foram revisados (mudanças críticas por humano, conforme a política);
- [ ] testes relevantes passaram no CI, com evidência do runner;
- [ ] acesso negativo foi testado (outro usuário, papel inferior, recurso bloqueado);
- [ ] contrato e interface cobrem os estados de falha;
- [ ] telemetria não contém dados privados;
- [ ] documentação mudou no mesmo PR (spec, ADR, `docs/references/sources.yml`, runbook, quando aplicável);
- [ ] existe procedimento de reversão ou recuperação compatível.

"Gerado por agente" não é um estado de qualidade.

## Template de feature (spec 21.3)

```markdown
## Objetivo
## Fora do escopo
## INV/API afetados
## Estados e transições
## Dados (tabelas, classificação, migração)
## Autorização (positivo e negativo)
## Concorrência (locks, versão, idempotência)
## Erros (códigos e Problem Details)
## UI (estados: loading, empty, error, offline)
## Testes (unidade, integração, HTTP, E2E; T-NN)
## Telemetria
## Migração e reversão
```

## Pull requests

- Uma coisa por PR; refatoração separada de mudança de comportamento.
- O PR lista arquivos alterados, testes executados, testes não executados e riscos remanescentes (o mesmo
  relatório que o agente entrega ao final, spec 14.1).
- Mudança em workflow, permissão ou regra de proteção vai em PR separado (spec 14.2).
- Teste intermitente tem dono, causa e prazo; rerun não substitui correção, e quarentena não esconde teste de
  privacidade ou segurança (spec 20.3).
