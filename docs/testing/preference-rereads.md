# Falhas de releitura após conflito — issue #151

Acompanhamento da [#142](https://github.com/BipoXDXD/duora/issues/142), concluída pelo
[PR #144](https://github.com/BipoXDXD/duora/pull/144). A cobertura anterior verifica gravações com erro e
412 seguido de revisão bem-sucedida. A #151 acrescenta a sequência **412 → revisão 503 → revisão
recuperada → salvamento explícito**; não repete a matriz de códigos nem amplia funcionalidades.

## Fronteira e matriz

`frontend/e2e/problem-rereads.spec.ts` executa o bundle de produção com componentes, cliente HTTP,
classificadores e traduções reais. Somente HTTP é interceptado; uma rota geral aborta chamadas de API
não previstas. Todos os dados são sintéticos, sem backend ou serviço real.

Relacionado a API-P03/P07, INV-05/INV-12 e T-23/A11Y. Cada recurso mantém sua versão independente e seus
próprios controles de revisão. A interface regional permanece somente manual, sem ativar matching,
detecção ou países adicionais.

| Recurso                    | Cobertura anterior preservada                                            | Acréscimo da #151                                                                                                                                                                                              |
| -------------------------- | ------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `/me/preferences`          | Gravações com 400/428/503; 412 com recarga bem-sucedida; 429/Retry-After | PUT 412/PRECONDITION_FAILED, GET de revisão 503/TEMPORARILY_UNAVAILABLE, rascunho de idiomas/opt-in mantido, salvar bloqueado, recuperação explícita e PUT com If-Match novo                                   |
| `/me/locale`               | Mesmos erros de gravação; códigos desconhecidos/herdados; revisão 412    | Mesma falha da revisão, dois alertas genéricos, escolha/idioma mantidos; novo envio explícito ainda usa versão antiga e recebe 412; revisão bem-sucedida restaura escolha salva; só o sucesso do PUT muda a UI |
| `/me/time-zone`            | Gravações e revisão de conflito; preservação de escolha                  | Falha de revisão mantém rascunho e bloqueio; recuperação mostra fuso salvo sem apagar a escolha; PUT explícito usa If-Match novo                                                                               |
| `/me/regional-preferences` | Escolha explícita, revisão 412 e canário completo de 429                 | Falha de revisão mantém países/rascunho e bloqueio; recuperação preserva escolha explícita e usa versão nova no PUT                                                                                            |

São **quatro cenários por idioma, oito por projeto e 24 novas execuções E2E**, em `pt-BR` e `en-US`, nos
três projetos existentes: Chromium, WebKit e mobile Chromium. O teste regional 412 existente recebe as
asserções completas no estado de conflito, antes da revisão, sem outro cenário equivalente. O canário
regional 429 e os demais casos anteriores permanecem.

## Asserções por etapa

- Cada resposta canário entregue registra método, caminho, status e código. A sequência esperada deve
  ser exatamente igual: PUT 412 e GET 503, mais o PUT 412 explícito adicional somente no formulário de
  idioma. Uma resposta ausente, extra ou para outro recurso falha. O helper anterior continua exigindo
  exatamente uma resposta para os cenários de uma única etapa.
- Mensagens literais locais, `html lang`, número e texto ordenado dos alertas. O idioma mantém dois
  alertas genéricos; os demais formulários mantêm conflito específico e falha genérica de revisão.
- Ausência de canário em texto/HTML do documento inteiro e de execução do payload; axe sem exclusões
  nos estados de conflito e falha da revisão, inclusive quando há dois alertas.
- Rascunhos e regras de bloqueio próprios de cada formulário. A revisão malsucedida não libera o
  salvamento dos formulários bloqueados; o idioma mantém a possibilidade de envio explícito com a
  versão-base anterior. Não se lê estado interno do React para fabricar uma prova de versão.
- Dez segundos de relógio simulado após a revisão malsucedida e após sua recuperação, com estabilização
  da rede, não geram PUT automático. As contagens incluem todos os PUT, não só os do recurso esperado.
- A revisão recuperada carrega versão `7`; a resposta ao salvamento explícito usa versão `8`. O request
  deve levar If-Match `"7"` e o rascunho esperado, com mensagens locais de sucesso. A escolha de idioma é
  restaurada na revisão e alterada novamente somente por ação explícita. Em preferências, a comparação
  exibe os idiomas e o opt-in salvos, diferentes do rascunho preservado.

## Validação e limites

Rodar tipos, lint, formatação, Vitest, build e Playwright, preservando os nove gates do repositório. O PR
registra comandos, resultados, commit, controles negativos e CI. A primeira execução encontrou uma
medição de contraste durante a transição CSS após reabilitar o botão no sucesso; o teste aguarda o fim
das animações existentes antes da leitura final, sem alterar estilos ou desabilitar regras do axe.

Três controles negativos temporários falharam nos dois idiomas no Chromium: omitir o recibo do GET,
usar versão antiga após a revisão e disparar PUT automático após a recuperação. As fontes foram
restauradas antes da validação final; esses controles demonstram sensibilidade às regressões
específicas, sem afirmar cobertura universal.

As respostas são sintéticas: não provam persistência, concorrência entre clientes reais, atomicidade,
autorização, cotas ou serviços externos. A ausência de repetição automática é observada nos intervalos
simulados, sem afirmar comportamento universal. Axe não substitui revisão manual com leitor de tela.
Não há novo contrato, catálogo, política, limite, dependência, workflow, detecção regional, ativação,
migração ou implantação. Falhas funcionais fora do escopo devem ser registradas separadamente, sem
reduzir testes. A reversão remove somente as alterações de testes/documentação.
