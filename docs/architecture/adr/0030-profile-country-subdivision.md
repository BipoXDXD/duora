# ADR-0030: País e subdivisão opcionais no perfil — expansão

- **Status:** Accepted (2026-10-21), expansão aditiva integrada pelo PR #110; sem implantação
- **Origem:** issue #78, ADR-0026, API-P01/API-P02; escopo autorizado de implementação aditiva
- **Revisar quando:** países de lançamento e fonte de manutenção forem decididos; antes do contract

## Contexto e decisão

Adotar a opção B da issue #78. Profiles é dono de `profiles.countries` e `profiles.subdivisions`, além das duas novas colunas do perfil. País ISO 3166-1 alfa-2 é opcional; subdivisão ISO 3166-2 de primeiro nível também. FK composta com MATCH SIMPLE (padrão PostgreSQL), FK de país, CHECK de presença de país e índice `(country_code, subdivision_code)` garantem integridade. Nenhuma cidade, endereço, coordenada, inferência de país ou serviço externo entra no fluxo.

Esta expansão preserva somente o catálogo que já existe: BR e as 27 UFs. Não representa aprovação dos países de lançamento. `ProfileLocationCatalog` é o catálogo de compatibilidade desta release, usado na validação, OpenAPI e modelo EF; a migração contém um snapshot literal para não mudar retroativamente. O cliente usa o contrato gerado e os rótulos já traduzidos. A escolha definitiva entre snapshots versionados e catálogo administrado em banco permanece aberta. Não há endpoint nem permissão administrativa nova de catálogo. `duora_app` tem somente SELECT nas tabelas de referência; a migração e a reconciliação de privilégios revogam DML, que fica com o migrador.

## Estado de implementação e validação (2026-10-21)

A expansão foi integrada à `main` pelo [PR #110](https://github.com/BipoXDXD/duora/pull/110), no commit `d6de23fe76d2dbe763e43d19ade4e4cf08c297e1`. O [CI após o merge](https://github.com/BipoXDXD/duora/actions/runs/39551321634) aprovou os nove checks: backend, frontend, e2e, secret-scan, contract, supply-chain, ops e imagens AMD64/ARM64. A aceitação deste ADR se limita à expansão e ao catálogo de compatibilidade; países adicionais e a manutenção definitiva do catálogo continuam pendentes na [issue #78](https://github.com/BipoXDXD/duora/issues/78).

O bloqueio inicial de vulnerabilidades do Pillow foi resolvido pelo [PR #107](https://github.com/BipoXDXD/duora/pull/107), incorporado antes do #110. O [CI da main após o #107](https://github.com/BipoXDXD/duora/actions/runs/39550420781) também passou nos nove checks. A falha local inicial de metadados de licenças não se reproduziu no CI; o gate remoto de licenças e os builds das imagens passaram. Isso não escolhe nem aprova a licença de uma futura fonte internacional do catálogo.

Não houve implantação nem aplicação da migração em produção. Antes da implantação, ainda é necessário medir locks, backfill e indexação em volume sintético representativo e ensaiar a transição/rollback com preservação dos dados.

## Contrato de transição

`regionCode` mantém exatamente o conjunto antigo e continua sendo aceito e retornado. Clientes antigos podem ler/editar no servidor novo. Uma UF antiga grava BR + UF; `regionCode: null` apaga os três campos. Alterar só nome/bio preserva a localização. Nas respostas novas, `regionCode` é null quando só o país foi informado.

O PATCH aceita `countryCode` e `subdivisionCode`. Os dois formam uma unidade de edição:

- omitir ambos mantém a localização;
- `{"countryCode":"BR"}` ou subdivisão null mantém apenas o país;
- `{"countryCode":"BR","subdivisionCode":"BR-SP"}` grava país + UF;
- `{"countryCode":null}` apaga a localização;
- subdivisão sem o campo countryCode e mistura de regionCode com campos novos são 400, mesmo se os valores coincidirem;
- país/subdivisão fora do catálogo ou par incoerente são 400 sem repetir valores enviados nos erros.

ETag/If-Match, 428, 412, CSRF, acesso ao próprio titular, autorização e no-store não mudam. O UPDATE permanece uma instrução SQL condicionada à versão. Não há leitura seguida de gravação para recompor a localização. Na SPA, editar qualquer seletor envia o par inteiro; conflitos preservam esse rascunho, enquanto campos não alterados não são reenviados.

## Migração e operação

1. Aplicar expansão por migrador após revisão: tabelas, seeds, colunas, constraints, índice e backfill na mesma transação. O backfill não muda `version` nem `updated_at`; null antigo permanece null.
2. A ponte de banco sincroniza INSERTs legados e alterações efetivas de region_code feitas pelo binário antigo durante a expansão. A API nova grava os três campos atomicamente.
3. Atualizar o servidor antes de servir a SPA nova. Confirmar a versão do servidor; a SPA nova lê respostas antigas de forma tolerante, mas o servidor antigo não aceita os campos novos.
4. Evitar servidores de versões diferentes gravando depois da ativação da SPA nova. Um binário antigo não distingue localização não informada de país sem subdivisão; seu UPDATE legado não permite distinguir um null explícito de um campo não alterado. Esta limitação não existe para clientes antigos falando com a API nova.
5. Rollback: preservar schema e dados; pausar edições de perfil se retornar ao binário antigo após uso dos novos campos. `Down` recusa perda de dados. Correção posterior é por migração forward. Nenhum deploy é parte desta alteração.
6. Contract separado remove region_code, CHECK e trigger somente após confirmar retirada dos consumidores antigos e aprovação de breaking change. Antes de habilitar outro país, revisar também projeção legada, códigos/labels dos contratos e testes.

A migração adquire locks e faz backfill/indexação transacionais: medir duração em ambiente sintético proporcional ao volume esperado antes da implantação. A etapa não acrescenta telemetria contendo localização ou valores recusados. Sucesso/falha continua nos mecanismos existentes.

## Alternativas

Código único não representa país sem subdivisão sem misturar níveis. Texto livre permite precisão excessiva. Catálogo dinâmico exige endpoint, cache, gestão de rótulos e governança ainda não decididos; não é necessário para esta expansão.

## Decisões de produto pendentes e recomendação

Nenhuma escolha adicional é necessária para revisar esta expansão BR-only. Para ativar países internacionais, o responsável deve aprovar a lista e o nível de subdivisão permitido por país; servidor em Boston e idioma inglês não provam que EUA são mercado habilitado. Se EUA forem aprovados, o recorte recomendado é 50 estados + DC; territórios com ISO 3166-1 próprio precisam de decisão própria. Não foi aplicado.

A revisão de elegibilidade e idade mínima por jurisdição mencionada na issue continua na lacuna J-05 do [mapa de dados](../../privacy/data-map.md#jurisdições-a-confirmar). Esta expansão não altera `age_band` nem resolve essa decisão ao habilitar a estrutura de localização.

Para manutenção inicial, recomendamos snapshot versionado revisado a partir do ISO, com responsável e revisão nas alterações do padrão, enquanto os países forem poucos. Antes de adotar uma distribuição concreta, confirmar procedência/licença, versão, rótulos PT-BR/EN-US e política para códigos retirados (não apagar códigos ainda referenciados). O snapshot BR desta etapa deriva do catálogo já presente; não alega atualização mundial do ISO.

## Compliance

Testes PostgreSQL reais cobrem backfill de todas as UFs/null com versão e timestamp preservados, FKs, ausência de país, valores desconhecidos, país somente e índice. Testes HTTP cobrem clientes legados/novos, null, conflitos e gravação condicional. SPA mantém minimização e revisão do conflito. OpenAPI/cliente regenerados e gate oasdiff conferem aditividade. A integração foi autorizada pelo usuário após os gates aprovados. A revisão humana de futuras migrações, da etapa contract e da implantação permanece exigida pela política do projeto.

## Adendo de 2026-10-21 — recorte e fonte aprovados, ativação pendente

O responsável aprovou explicitamente EUA e Brasil, ISO como referência, CLDR 48.2 como distribuição inicial e `Unicode-3.0` como licença, na continuação da issue #78. Este adendo registra a decisão posterior; as seções anteriores preservam o estado e as pendências da expansão integrada pelo PR #110.

O recorte aprovado para a próxima etapa é `US` com 50 estados + Distrito de Columbia (51 subdivisões), e `BR` com as 27 UFs: dois países e 78 subdivisões de primeiro nível. Territórios com código próprio, como PR, GU e VI, não entram nesse recorte. País e subdivisão continuam opcionais; localização declarada não comprova residência, nacionalidade nem elegibilidade.

A fonte escolhida é um snapshot versionado da [release CLDR 48.2](https://github.com/unicode-org/cldr/releases/tag/release-48-2), conferido contra [ISO 3166](https://www.iso.org/iso-3166-country-codes.html), com licença [Unicode-3.0](https://raw.githubusercontent.com/unicode-org/cldr/release-48-2/LICENSE). A implementação futura deve preservar origem, versão, hashes, avisos de copyright/permissão e alterações editoriais, e gerar os artefatos do produto a partir de uma fonte interna única. A política/licença foi integrada separadamente pelo [PR #117](https://github.com/BipoXDXD/duora/pull/117), após os nove checks aprovados; seu escopo e a proveniência conferida ficam em [catalog-licenses.md](../../security/catalog-licenses.md).

O CLDR não é uma lista pronta de países habilitados pelo produto: o importador futuro deve selecionar apenas o recorte aprovado, normalizar/conferir os códigos ISO e tratar códigos adicionais ou depreciados. Os rótulos portugueses da release consultada são provisórios e precisam de revisão editorial PT-BR/EN-US. Códigos ainda referenciados não devem ser apagados ou reatribuídos automaticamente. Outra release, fonte, licença ou ampliação de países exige nova revisão.

O usuário não aprovou responsáveis nominais nem a cadência definitiva de manutenção. A revisão mensal de releases/avisos permanece recomendação. Continuam pendentes a designação técnica/editorial, a revisão dos rótulos, a elegibilidade/idade mínima por jurisdição (J-05), a implementação/inventário do snapshot, os ensaios operacionais e a etapa contract.

**A aprovação não habilita países nem autoriza implantação.** O catálogo executável continua somente BR + 27 UFs. Seeds, tabelas, validações, OpenAPI e SPA não mudam por este adendo. A inclusão de US no produto depende de autorização posterior e PR de implementação/testes; a implantação depende de autorização própria. O plano de transição e rollback acima continua aplicável, inclusive a pausa de edições ao retornar ao binário antigo e a preservação de schema/dados.
