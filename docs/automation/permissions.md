# Política de permissões da automação

Fonte: spec 1.3, 14.2–14.4 e 17.1. Decisão: [ADR-0014](../architecture/adr/0014-automacao-com-gates.md).
Invariante protegida: INV-12 (nenhum agente de desenvolvimento tem autorização irrestrita em produção),
verificada por T-12 ([acceptance.md](../testing/acceptance.md)).

Este arquivo é **área protegida**: mudar uma permissão aqui é mudança de política, com PR próprio e aprovação
do responsável técnico.

## Matriz (spec 14.2)

| Recurso/ação | Agente de código | Automação de CI/ops | Aprovação necessária |
|---|---|---|---|
| Ler código e documentação pública | Permitido no escopo da tarefa | Permitido | Não, salvo material restrito |
| Escrever em branch de trabalho | Permitido | Permitido para automações identificadas | Merge protegido |
| Executar testes locais isolados | Permitido | Permitido | Sem dados ou segredos reais |
| Adicionar dependência | Propor com justificativa e licença | Auditoria automática | Revisão de dependência |
| Alterar autenticação, bloqueio ou autorização | Propor | Testar | Revisão humana e testes de segurança |
| Alterar workflows, permissões ou regras de proteção | Propor em mudança separada | Não autoaprovar | Responsável técnico |
| Criar migração | Gerar proposta e SQL | Ensaiar em banco descartável | Revisão antes de produção |
| Aplicar migração destrutiva | Proibido | Só procedimento aprovado | Aprovação explícita e recuperação testada |
| Consultar dados de participantes | Proibido por padrão | Só serviço autorizado à finalidade | Acesso excepcional auditado |
| Deploy em homologação | Via pipeline | Automático após os gates | Não exige acesso SSH amplo |
| Deploy em produção | Não diretamente | Executor restrito | Gate de release no piloto |
| Banir permanentemente, decidir recurso ou aprovar evidência sensível | Proibido autonomamente | Preparar triagem | Responsável humano |

## Credenciais e ambientes

| Item | Regra |
|---|---|
| Workspace do agente | Descartável, dados sintéticos, sem a home inteira montada, sem chaves pessoais, sem acesso ao Docker socket do host de produção (spec 2.5, 14.3) |
| Docker local | Superfície privilegiada; o socket fica restrito ao runner autorizado |
| Tokens | Curta duração e escopo estreito quando o serviço oferecer |
| GitHub Actions | Permissões mínimas por job; actions de terceiros fixadas por SHA completo; checkout de código não confiável nunca em job com segredos (spec 17.1) |
| Runners | Runners de pull request não ficam no host de produção nem recebem seus segredos |
| Executor de deploy | Comando restrito que recebe release aprovada e valida manifesto e digests; sem `sudo ALL`, shell arbitrário ou acesso amplo ao Docker socket (spec 15.5) |
| Acesso administrativo × CI | Credenciais separadas; SSH com host key validada, nunca `StrictHostKeyChecking=no` (spec 17.2) |

## Automação de dependências

Pode abrir PRs e agrupar patches de baixo risco. Auto-merge só para classes explicitamente permitidas, depois de
todos os gates, e nunca em autenticação, banco, infraestrutura ou contratos incompatíveis. Atualização de
major nunca é silenciosa (spec 14.2).

## Área protegida

Exigem revisão do responsável técnico e não podem ser alterados como efeito colateral de outra tarefa:

- [`AGENTS.md`](../../AGENTS.md), este arquivo e [workflow.md](workflow.md);
- `.github/CODEOWNERS`, `.github/workflows/` e regras de proteção de branch;
- suítes de segurança e de arquitetura (`backend/tests/Duora.ArchitectureTests` e os testes de T-01 a T-28).

Uma alteração que reduz teste, scanner ou gate é mudança de política, nunca correção incidental do código que
estava falhando (spec 14.3).

## Operação automatizada (spec 14.4)

Workflows determinísticos e runbooks restritos; nenhum "agente supervisor" com comando arbitrário. A análise de sinais
não autoriza exclusões, mudanças de firewall, restaurações ou sanções fora dos runbooks aprovados.

| Processo | Automação | Para e escala quando |
|---|---|---|
| Check-in e rodadas | Jobs duráveis, relógio persistido, transições idempotentes | Inconsistência de estado, atraso excessivo ou modo de manutenção |
| Pareamento | Algoritmo determinístico com elegibilidade revalidada | Falha de segurança, capacidade excedida ou nenhum par elegível |
| Notificações | Outbox, deadline, deduplicação e preferências | Destinatário bloqueado, deadline vencido ou fornecedor degradado |
| Imagens | Quarentena, validação técnica e derivados | Conteúdo suspeito, decompression bomb ou falha de revisão |
| Retenção | Exclusão em lotes e trilha de execução | Preservação aprovada, divergência de tombstone ou falha de fornecedor |
| Backups | WAL contínuo, agenda e verificação | Atraso de archive, erro de integridade ou restore de teste falho |
| Infraestrutura | Reaplicação idempotente de configuração aprovada | Drift inesperado em privilégios, rede ou armazenamento |
| Patches | Detecção, PR e homologação | Incompatibilidade, vulnerabilidade sem mitigação ou regressão |
| Incidentes | Detectar, alertar, coletar diagnóstico sanitizado | Ação destrutiva, rotação crítica ou suspeita de vazamento |
