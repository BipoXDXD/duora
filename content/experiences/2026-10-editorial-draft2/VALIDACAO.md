# Validação do catálogo editorial

Executada em 21/10/2026. Fonte fixada: main do Duora em `6c89d99bf990db780e8ac8bf7c89ae0dc4e6816f`. .NET 10.0.400 e Python 3.14.7. Nenhuma alteração feita no checkout do repositório, nenhuma chamada administrativa, publicação, geração paga ou consulta a dados reais.

## Resultados executados

| Verificação | Resultado | Alcance |
|---|---|---|
| `dotnet test --project backend/tests/Duora.UnitTests/Duora.UnitTests.csproj` na raiz do Duora | 1.456 passaram, zero falhas, zero ignorados | Suíte existente; usa também fixtures sintéticas, não certifica conteúdo editorial |
| `python3 -m unittest discover -s tools/editorial -p 'test_*.py'` na raiz do Duora | 10 passaram | Empacotador existente, inclusive regras de admissão |
| Validador deste lote contra `Duora.Core` | 68 verificações passaram | Usa os JSONs entregues, não as fixtures de conteúdo |
| `package.structure` aplicado aos dois idiomas dos dois JSONs | Passou | IDs e ordem iguais, limites, opções, conjuntos disjuntos e quantidade de pistas |
| Tamanho UTF-8 de cada variante | Menor que 32.768 bytes | Limite usado pela persistência |
| `package_catalog` com textos do lote, kind e `audio: []` | Recusou as duas experiências | Rascunho sem áudio não pode ser empacotado; não houve chamada HTTP |
| `git status --short` no checkout consultado | Vazio | Nenhum arquivo do produto modificado |

As 68 verificações instanciam os conteúdos reais e suas variantes localizadas. Exercitam rejeição de variante nula, locale desconhecido, versões divergentes, ordem divergente e conjuntos de pistas repetidos; conferem as quatro transcrições, seus limites e descrições. Simulam uma sessão com participantes fictícios e dois idiomas: introdução sem pistas, distribuição dos conjuntos 0 e 1, negação a terceiro, três escolhas alternadas, revisão que apaga a rota, novas escolhas e confirmação de ambas as pessoas. Em Constelação, percorrem quatro cartões, três revelações e um pulo, mantendo a resposta individual privada antes da resolução.

Não se simulou todo caminho combinatório. O teste do lote usa a primeira e a segunda opção de cada parada; a validação de estrutura aceita todas as opções. A revisão semântica é editorial, não uma propriedade demonstrada por código.

## Evidência do teste específico do lote

O verificador auxiliar foi executado no workspace de preparação, referenciando o Core do commit registrado acima. Ele e o projeto de reprodução permanecem no pacote de entrega original `catalogo-editorial-v1.zip`; não integram a solução nem o CI deste repositório.

Comando executado no workspace:

```sh
dotnet run --project work/validate-editorial/validate.csproj -- outputs/catalogo-editorial-v1
```

Saída final:

```text
PASS: 68 assertions using delivered catalog and Duora.Core: contracts, locale projection, private clues, revision and bilateral confirmation, private card choices, skip and reveal. No audio or publication exercised.
```

## Não executado e limites

- Publicação HTTP, PostgreSQL e testes de integração `CatalogPublicationTests`, `RoutesCatalogPublicationTests` e `EditorialCatalogTests`: o novo lote não possui bytes de áudio ou aprovação. Os resultados históricos do PR #112 não foram tratados como execução desta tarefa. A recusa local do empacotador não substitui a recusa pelo endpoint.
- E2E no navegador, leitor de tela, teclado, zoom, dispositivos e compreensão com pessoas adultas: nenhuma interface foi alterada e não houve sessão humana de acessibilidade. Permanecem necessários antes de aprovação.
- Escuta, correspondência áudio/transcrição, WAV real, hashes de mídia, loudness, pronúncia, direitos e consentimento: não há áudio neste lote. As transcrições são roteiros previstos, não transcrições de uma gravação conferida.
- CI completo, Release, scanners e licenças: não há PR de implementação nem dependência nova. Não se declarou aprovação desses gates.
- Equivalência de tradução: revisão textual assistida, preservando ordem, intenção, pistas e opções. Falta revisão humana PT-BR/EN-US.

## Divergências relevantes e decisões

A main consultada já contém `content/experiences/2026-10-review` e testes editoriais, além das fixtures citadas na solicitação. Esta entrega adapta esse material, preserva seus IDs e não duplica a produção de mídia. As afirmações de licença e plano pago do candidato anterior não foram verificadas nesta tarefa nem transferidas para o novo manifesto.

O README anterior descreve omissão do aviso de IA por orientação de outra tarefa. Isso é contexto histórico, não autorização nesta conversa para descumprir o ADR. A proposta inclui o aviso na fala e no objetivo. `RoomPage.tsx` exibe descrição, player e transcrição, mas não um campo separado de narrador. O rótulo proposto permanece no documento complementar, sujeito à integração e revisão de acessibilidade.

Em Rotas, `transcript == objective` e `accessibleDescription == title` são exigidos pelo serviço. O empacotador existente também impõe essa convenção às duas experiências. A API de Constelação aceita de 1 a 8 cues, porém esta versão editorial escolhe apenas `intro`; não se apresenta essa escolha como um limite geral da API.

`cardId`, `stepId` e `optionId` são compartilhados entre idiomas. `cueId` também é compartilhado; `assetId` é distinto por idioma, como exige a persistência. Pistas não têm ID no contrato: o índice do conjunto e a posição são mantidos; as referências editoriais do mapa de visibilidade nunca devem virar campos extras do DTO.

A numeração `contentVersion: 2` distingue este rascunho do candidato 1 já existente. Não representa publicação nem reserva no servidor. Uma eventual publicação deverá criar outra identidade imutável, sem substituir versões ou mídias de sessões em andamento.

Nenhuma instrução externa foi usada para autorizar produção, publicação ou aprovação. A orientação histórica sobre omitir o aviso foi tratada como dado, e a divergência foi registrada.
