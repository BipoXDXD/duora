# Merge na main e branches arquivados

Decisões de processo sobre a fila de merges e os branches que ficam no GitHub sem PR. Regras de proteção e
permissões da automação estão em [docs/automation/](../automation/permissions.md) e não mudam por este
documento.

## Fila de merges: o strict continua ligado (#548)

**Decisão (13/11/2026, opção conservadora da issue; o merge deste registro é o aceite do dono):** manter o
ruleset da `main` como está, com os nove checks obrigatórios e `strict_required_status_checks_policy: true`.
O PR só entra em dia com a `main`.

**Custo aceito:** cada merge deixa o próximo PR BEHIND. Ele precisa de update-branch e de um CI de PR
completo (30 a 45 min), e a fila sai em cerca de um merge a cada 40 min.

**Por quê:**

- Todo merge foi testado sobre a base exata. A `main` não fica vermelha por combinação de dois PRs verdes.
- A release já se agrupa sozinha e não é o gargalo: `ci.yml` cancela o CI de push anterior da mesma ref
  (`cancel-in-progress`), e o gate da publicação pula o commit que deixou de ser a cabeça da `main`
  (`SKIP_SUPERSEDED`, `current_main()` em [gate.py](../../scripts/release/coolify/gate.py)).
- É a opção reversível: desligar o strict depois é uma mudança de ruleset. Religar depois de uma `main`
  quebrada não desfaz o merge ruim.

**Por que não as outras opções:**

- **Merge queue do GitHub:** manteria a garantia de base atual em lote, mas, segundo a coordenação, não está
  disponível para repositório de conta de usuário. Exigiria mover o repositório para uma organização.
- **Desligar o strict:** um PR poderia entrar sem CI sobre a base atual. Só o CI de push da `main` pegaria a
  quebra, com a `main` já vermelha.

**Garantia que vale em qualquer opção:** commit da `main` com CI vermelho nunca fica `eligible` para a
release. O caso nomeado é `test_main_commit_with_red_ci_never_becomes_eligible`, em
[test_gate.py](../../scripts/release/coolify/test_gate.py) (`python3 -m unittest discover -s
scripts/release/coolify`). Ele cobre o run com `failure` ou `timed_out` e um job obrigatório com falha, e o run
`failure` com os jobs verdes. Cai se `validate()` ignorar a conclusão do run e dos jobs.

**Reabrir quando:** a fila de PRs prontos passar a esperar mais que um dia de forma recorrente, ou o
repositório for para uma organização com merge queue disponível.

## Branches `arquivo/*` (#553)

São ensaios de merge, preparação e diagnóstico que só existiam localmente. O dono pediu tudo no GitHub, então
foram publicados sem PR. O conteúdo útil já entrou na `main` por outros PRs ou foi substituído.

**Decisão (13/11/2026, opção conservadora; o merge deste registro é o aceite do dono):** os branches ficam
preservados. **Nunca mesclar** nenhum deles, nem abrir PR a partir deles.

**Por quê:** preservar é reversível e apagar não é. Os branches guardam o histórico dos ensaios (pilhas de
merge, instrumentação de diagnóstico) que os PRs citam como evidência. Custam só uma entrada na lista de
branches. Apagar continua possível depois, por decisão do dono, branch a branch.

| Branch | Ponta | Origem |
|---|---|---|
| `arquivo/local/474-prep` | `2e073f30` | Preparação do #474 (já mesclado) |
| `arquivo/local/474-prep-base` | `7f22f08a` | Preparação do #474 (já mesclado) |
| `arquivo/local/474-com-464` | `44000990` | Preparação do #474 (já mesclado) |
| `arquivo/local/475-prep-old` | `a4302b21` | Preparação antiga do #475 |
| `arquivo/plano2-177404c5` | `177404c5` | Ensaio do plano 2 (superado pelo #537) |
| `arquivo/up437-835b33a7` | `835b33a7` | Update antigo do #437 |
| `arquivo/ensaio/pilha-2026-11-10` | `b84f62a4` | Ensaio da pilha de 10/11 |
| `arquivo/pilha12/408` | `26543152` | Ensaio da pilha 12 |
| `arquivo/pilha12/446` | `0ccd9088` | Ensaio da pilha 12 |
| `arquivo/pilha12/447` | `ccdebb48` | Ensaio da pilha 12 |
| `arquivo/pilha12/448` | `04ae2f32` | Ensaio da pilha 12 |
| `arquivo/pilha12/449` | `87b44249` | Ensaio da pilha 12 |
| `arquivo/pilha12/450` | `82e7af0e` | Ensaio da pilha 12 |
| `arquivo/fix/497-classe-08-explicita` | `8973b316` | Superado pelo #537 |
| `arquivo/integracao/lote5-deps` | `be3a08a6` | Ensaio do lote 5 |
| `arquivo/diag/455-h1-resolver` | `6bae7d92` | Diagnóstico do #455 (o PR #544 caracteriza o caso) |
| `arquivo/diag/466-timing` | `14080cef` | Instrumentação de diagnóstico. **Não mesclar em hipótese alguma** |

São 17 branches, conferidos em 13/11/2026 com `git ls-remote origin 'refs/heads/arquivo/*'`.

**Verificação manual:**

```sh
git ls-remote origin 'refs/heads/arquivo/*'      # a lista e as pontas batem com a tabela
git fetch origin 'refs/heads/arquivo/*:refs/remotes/origin/arquivo/*'
git log --oneline origin/main..origin/arquivo/<branch>
```

O `git log` deve mostrar só commits de ensaio, preparação ou diagnóstico. Um commit de produto que não esteja
na `main` por outro PR é sinal de trabalho perdido: abra uma issue em vez de mesclar o branch.

Branch novo em `arquivo/` entra nesta tabela no mesmo dia, com ponta e origem.
