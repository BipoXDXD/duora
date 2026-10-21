# Regressões dos callbacks da selfie — issue #150

Acompanhamento da [#142](https://github.com/BipoXDXD/duora/issues/142), concluída pelo
[PR #144](https://github.com/BipoXDXD/duora/pull/144). Estes testes acrescentam cobertura dos callbacks
depois da abertura da captura; não repetem os canários de início/abertura nem o cenário de expiração.

## Fronteira dos testes

`frontend/src/features/account/SelfieCallbacks.test.tsx` monta a rota real com `SelfieVerificationPage`,
`SelfieCapture`, cliente HTTP, parsers e traduções existentes. Somente `FaceLivenessDetectorCore` é
substituído por um componente sem mídia, que disponibiliza os callbacks recebidos do wrapper. O MSW
intercepta o HTTP e recusa requisições sem handler. Nenhuma câmera, credencial real, captura, AWS ou
fornecedor é utilizado. Não há novo hook ou atalho no código de produção.

Relacionado a API-P05/P06, INV-05/INV-12 e T-23/A11Y da matriz de aceite. O uso de identificadores distintos
para conta, tentativa e sessão do SDK permite detectar confusão entre eles no envio da conclusão.

## Matriz e cobertura anterior

| Caminho | Cobertura anterior | Acréscimo da #150 |
|---|---|---|
| Início e abertura da selfie | Canários bilíngues E2E da #142, incluindo axe e ausência de pedido de câmera | Mantidos, sem duplicação |
| Expiração e nova tentativa | Teste de componente já existente confirma novas credenciais/sessão | Mantido sem alteração |
| `onAnalysisComplete` → POST `/me/selfie-verifications/{id}/complete` | Mock anterior não invocava o callback | 404/NOT_FOUND com canário em title/detail/errors; recibo da resposta interceptada; tentativa, corpo e CSRF corretos; promise resolvida pelo tratamento real |
| `onUserCancel` | Sem regressão do callback pelo wrapper real | Limpa/desmonta captura, informa localmente e não envia conclusão |
| `onError` do SDK | Sem regressão do encaminhamento pelo wrapper real | Erro sintético com texto canário; mesma limpeza/continuação segura, sem conclusão |

São **três cenários por idioma, seis testes de componente**, em `pt-BR` e `en-US`. Cada cenário exige o
aviso informativo local vigente (`role=status`), `html lang`, ausência de falso sucesso, de canário no
texto/HTML do documento inteiro e de propriedade de execução do payload. Os controles reais de abertura
e reabertura são acionados com Enter; nomes acessíveis e link de retorno também são verificados. Reabrir
faz uma nova requisição de captura e remove o aviso anterior. Nenhum status/código ganha mensagem nova.

## Validação

Rodar `pnpm test src/features/account/SelfieCallbacks.test.tsx`, `pnpm typecheck`, `pnpm lint`,
`pnpm format:check`, `pnpm test`, `pnpm build` e `pnpm e2e`. O relatório do PR registra os comandos,
resultados, commit validado, controles negativos e CI. Os nove gates existentes continuam obrigatórios.
Alterações temporárias de controles negativos devem ser restauradas antes da validação final/commit.

Três controles negativos locais confirmaram a sensibilidade dos dois idiomas: enviar o identificador
da conta em vez da tentativa na conclusão, suprimir o aviso de falha e descartar `onError` no wrapper.
Cada alteração provocou duas falhas esperadas no cenário correspondente. As fontes de produção foram
restauradas antes da execução final; nenhuma dessas alterações integra o PR.

## Limites e reversão

Os novos testes são de componente, em jsdom. Ausência de canário no DOM é verificada, mas jsdom não
comprova execução de eventos de mídia, CSP, encerramento de streams ou acessibilidade do SDK em navegador
real. Não se adicionaram novos E2E; os projetos Chromium, WebKit e mobile Chromium e as verificações axe
existentes permanecem. A verificação de papéis, nomes e teclado não equivale a aprovação WCAG ou ensaio
com leitor de tela.

As respostas sintéticas não comprovam autorização/cotas reais, persistência, qualidade/presença,
retenção, cobrança, IAM, dispositivos físicos ou funcionamento do fornecedor. A homologação permanece
na [#93](https://github.com/BipoXDXD/duora/issues/93); estes testes não autorizam ativação ou implantação.
Não há alteração funcional, contrato, catálogo, limite, política, dependência ou workflow. A reversão
remove somente os novos testes e este registro, sem migração ou mudança operacional.
