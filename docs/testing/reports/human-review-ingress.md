# Ingresso sintético no fluxo de revisão humana — #173 / #69

Este recorte implementa o ingresso descrito no ADR-0035 somente para dados sintéticos em
`Development` e `Testing`. `Production`, `Staging` e nomes desconhecidos continuam recusados no domínio e
na validação compartilhada de Web/Worker. A ausência de `Media:Review` continua desligando uploads e o
consumidor técnico; nenhuma configuração de release é alterada.

## Comportamento e fronteiras

Antes, toda sanitização bem-sucedida chegava à criação de um asset aprovado. Com `HumanReview`, o
dispatcher chama `PhotoReviewCandidates.StageAsync` antes de qualquer gravação em derivados finais.
O candidato sanitizado fica privado; a transação existente registra `AwaitingReview`, caso, auditoria e
obrigação durável de apagar o original. O sucesso encerra a entrega técnica; nenhuma espera por uma
pessoa mantém o lease de sanitização aberto. Não há asset Approved nesse percurso.

O prazo de preparação é o menor entre o vencimento do lease calculado antes do claim e o deadline
técnico da entrega. Ele é transportado até o staging, sem calcular um novo orçamento após o decoder.
Perda desse prazo/versão não encerra uma entrega ainda pertencente a outra tentativa. Falha de storage
segue o retry limitado existente e conserva a obrigação de limpeza dos candidatos incompletos.

`Processing → AwaitingReview` passa a ser uma transição explícita. As transições genéricas
`AwaitingReview → Approved/Rejected` continuam negadas: a decisão pertence às operações autorizadas de
Safety e às suas guardas/auditoria. `AwaitingReview` continua ocupando as cotas existentes, sem aumentar
os limites. O percurso `TechnicalOnly` permanece exclusivo de dados sintéticos.

Não há nova migração, tabela, endpoint, DTO, permissão SQL, grant, montagem ou dependência. O decoder,
launcher, receiver/installer e a composição privada de revisão permanecem com seus contratos atuais.

## Verificação

O PR #263 começa pelo head test-first `e4cb3dd4d8ad9a5570d32f8dc6ee0d53a0163f0b`, sobre A0A
`a0a54e66be24105bed10313438951490845e979b`. Os dois arquivos novos definem 13 casos:

Nesse head, o CI `40318088324`, job backend `121555652873`, observou o RED esperado: 4.039 testes,
4.026 aprovados, 13 falhas e zero skips. As nove falhas unitárias e quatro de integração ocorreram na
asserção de `Enum.TryParse` pela política ainda ausente; build e formatação passaram. A implementação
preserva esses dois arquivos e incorpora a base F3 do #261 antes de sua própria qualificação.

| Cobertura | Efeito que precisa ser observado |
|---|---|
| Nove casos unitários de política/estado | HumanReview sintético explícito, veredito AwaitingReview e recusa de ambientes não admitidos |
| Pipeline com decoder isolado existente | Caso/candidato/auditoria privados, nenhum asset/derivado final, original apagado, outbox entregue e nenhuma duplicação no próximo passo |
| Falha no segundo PUT e retry | Original preservado enquanto a preparação falha, limpeza durável, nenhum caso/asset antes do commit e posterior ingresso privado único |
| Lease vencido após sanitizar | Nenhum PUT, candidato, caso ou asset; original permanece para recuperação |
| Deadline técnico anterior ao lease | O mesmo bloqueio mesmo quando o lease ainda está válido |

A sentinela unitária reader-only anterior evolui explicitamente para admitir apenas a nova transição
de ingresso; mantém as negativas de aprovação/reprovação genéricas. A matriz existente de transições e
de cota inclui o novo estado sem remover casos anteriores.

Os resultados de execução pertencem aos runs e heads exatos do PR. Revisão estática não demonstra
RED/GREEN. Build, formatação e testes novos são executados pelo CI Linux normal, com seus gates
preservados; não há novo workflow, job, dispatch ou qualificação local no Mac.

## Limites de aceite e reversão

Este ingresso sintético não admite HumanReview em produção e não encerra as issues #173/#69.
Continuam próprios os aceites de cobertura humana independente, agendamento/retomada dos prazos
editoriais e de recurso, ciclo de exclusão/leitura de fotos, B2/retenção/permissões, exportação e restore
Approved-only com tombstones, montagens/media-verify e capacidade #86/#174.

O interruptor vazio impede novas admissões; ele não desfaz casos já gravados. Uma reversão conserva
leitores compatíveis com AwaitingReview e com os registros editoriais já aditivos. Não reclassificar
pendências como Approved, apagar histórico ou usar TechnicalOnly em produção como atalho. Qualquer
integração que publique automaticamente exige a janela coordenada com o operador 69; não habilita a
funcionalidade nem autoriza ações manuais na VPS.
