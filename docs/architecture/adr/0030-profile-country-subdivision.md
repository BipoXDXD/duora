# ADR-0030: País e subdivisão opcionais no perfil — expansão

- **Status:** Proposed (2026-10-21), implementação para revisão humana
- **Origem:** issue #78, ADR-0026, API-P01/API-P02; escopo autorizado de implementação aditiva
- **Revisar quando:** países de lançamento e fonte de manutenção forem decididos; antes do contract

## Contexto e decisão proposta

Adotar a opção B da issue #78. Profiles é dono de `profiles.countries` e `profiles.subdivisions`, além das duas novas colunas do perfil. País ISO 3166-1 alfa-2 é opcional; subdivisão ISO 3166-2 de primeiro nível também. FK composta com MATCH SIMPLE (padrão PostgreSQL), FK de país, CHECK de presença de país e índice `(country_code, subdivision_code)` garantem integridade. Nenhuma cidade, endereço, coordenada, inferência de país ou serviço externo entra no fluxo.

Esta expansão preserva somente o catálogo que já existe: BR e as 27 UFs. Não representa aprovação dos países de lançamento. `ProfileLocationCatalog` é o catálogo de compatibilidade desta release, usado na validação, OpenAPI e modelo EF; a migração contém um snapshot literal para não mudar retroativamente. O cliente usa o contrato gerado e os rótulos já traduzidos. A escolha definitiva entre snapshots versionados e catálogo administrado em banco permanece aberta. Não há endpoint nem permissão administrativa nova de catálogo. `duora_app` tem somente SELECT nas tabelas de referência; a migração e a reconciliação de privilégios revogam DML, que fica com o migrador.

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

Para manutenção inicial, recomendamos snapshot versionado revisado a partir do ISO, com responsável e revisão nas alterações do padrão, enquanto os países forem poucos. Antes de adotar uma distribuição concreta, confirmar procedência/licença, versão, rótulos PT-BR/EN-US e política para códigos retirados (não apagar códigos ainda referenciados). O snapshot BR desta etapa deriva do catálogo já presente; não alega atualização mundial do ISO.

## Compliance

Testes PostgreSQL reais cobrem backfill de todas as UFs/null com versão e timestamp preservados, FKs, ausência de país, valores desconhecidos, país somente e índice. Testes HTTP cobrem clientes legados/novos, null, conflitos e gravação condicional. SPA mantém minimização e revisão do conflito. OpenAPI/cliente regenerados e gate oasdiff conferem aditividade. Revisão humana obrigatória antes de merge e implantação.
