# ADR-0026: Público principal fora do Brasil, produto inteiro em PT-BR e EN-US e servidor em Boston (EUA)

- **Status:** Accepted (2026-10-19), decidido pelo dono em 2026-10-19
- **Origem:** spec 1.1, 12.1, 12.2, 13.6, 15.1 e 23.2; [ADR-0010](0010-object-storage-externo-leitura-via-api.md)
  (adendo das fotos na VPS), [ADR-0022](0022-i18n-intl-messageformat-catalogo-tipado.md) (idiomas),
  [ADR-0023](0023-topologia-do-backup-repository-host-e-object-lock.md) (adendo do B2) e
  [ADR-0024](0024-fluxo-de-verificacao-etaria.md) (verificação etária); PR #58 (`region_code` como UF) e PR #61
  (transferência internacional do backup)
- **Revisar quando:** os países do lançamento forem fixados, entrar um terceiro idioma, o servidor ou o backup
  mudarem de país, ou o apoio jurídico concluir a análise das lacunas abaixo

## Contexto

A spec e os documentos derivados foram escritos supondo um público brasileiro: a LGPD era a única lei citada, a
"transferência internacional para os EUA" aparecia como exceção (só o backup saía do país), a região do perfil
ficou restrita às 27 UFs (PR #58), a interface era "inicialmente em PT-BR" (spec 13.6) com o inglês como segundo
idioma da interface (ADR-0022), e a região da VPS era "próxima da maioria das pessoas do piloto" (spec 15.1), sem
país.

O dono decidiu, em 2026-10-19:

1. O produto inteiro funciona em **PT-BR e EN-US**: interface, e-mails, conteúdo das experiências, documentos
   voltados ao usuário (termos, política de privacidade, ajuda) e mensagens de erro.
2. O servidor fica em **Boston, EUA** (VPS da Hostinger). O backup B2 fica em **US East**.
3. O público-alvo principal **não é brasileiro**.

Os pressupostos contrários encontrados em `origin/main` (71381f2) estão no
[levantamento](#levantamento-dos-pressupostos-contrários) ao fim deste ADR.

Alternativas consideradas para o tratamento documental:

- **Reescrever a spec e os ADRs aceitos para o novo público.** Descartada: o README dos ADRs proíbe reescrever um
  ADR aceito, e a spec é a base de referência dos IDs (`INV`, `API`, `T`); reescrevê-la apagaria o rastro de por que
  cada controle existe.
- **Só um adendo no ADR-0022.** Descartada: a decisão atravessa privacidade, modelo de dados, operação e produto; o
  ADR-0022 é sobre a biblioteca de i18n da SPA.
- **Este ADR, com divergências registradas na spec e adendos curtos nos ADRs afetados.** Escolhida: é o padrão que o
  repositório já usa (ADR-0024 e spec 12.2).

## Decisão

Vamos tratar o Duora como um produto **internacional com dois idiomas**, cujo público principal está fora do
Brasil e cuja infraestrutura está nos EUA:

- **Idiomas.** PT-BR e EN-US são os dois idiomas do produto inteiro, com o mesmo conjunto de textos: nenhum texto
  voltado à pessoa usuária existe só num deles. As tags BCP 47 são `pt-BR` e `en-US`; o `en` do ADR-0022 passa a
  ser `en-US` (a mudança de código está na [proposta 3](#3-textos-da-interface-dos-e-mails-e-dos-erros)). Conteúdo
  de experiência, e-mail, erro exibido e documento legal só é publicado com os dois idiomas revisados.
- **Alcance de "projeto inteiro" (pendente de confirmação do dono).** O dono disse "o projeto inteiro"; este ADR
  aplica a decisão a tudo que a pessoa usuária vê (interface, e-mails, conteúdo, documentos voltados ao usuário e
  mensagens de erro). Se ela vale também para documentação interna, ADRs, mensagens de commit, nomes de teste e
  descrições do OpenAPI (hoje em português, por exemplo `ProfileEndpoints.cs:26-32`, `:70`, `:93`, `:113`) **não foi
  decidido**: até a resposta do dono, esses artefatos seguem em PT-BR, e o item correspondente da issue #80 fica
  dependente dela.
- **Público e jurisdições.** O público principal está fora do Brasil; os países do lançamento ainda não foram
  fixados (lacuna J-01). O Brasil é país provável (J-01); a aplicabilidade da LGPD, inclusive a quem está fora do Brasil, é a confirmar (J-02). As leis prováveis para o público fora do
  Brasil ficam mapeadas como **lacunas a confirmar com apoio jurídico** em
  [data-map.md](../../privacy/data-map.md#jurisdições-a-confirmar); este ADR não conclui nada sobre aplicabilidade.
- **Região do servidor.** A VPS de produção fica em Boston (EUA); o backup (pgBackRest e restic) fica no Backblaze
  B2 US East. Banco, fotos e backup estão todos nos EUA (fato), inclusive os dados de pessoas no Brasil, desde o
  primeiro request. Para esses dados, se isso é transferência internacional (LGPD art. 33) ou coleta internacional (Resolução CD/ANPD 19/2024, a confirmar) e qual salvaguarda se aplica é a confirmar (J-02); o registro deixa de ser só o do backup
  ([data-map.md](../../privacy/data-map.md#local-do-tratamento-e-transferências-a-confirmar)).
- **Formatos.** Instantes continuam em UTC na API e no banco; apresentação por `Intl` no idioma ativo e no fuso da
  pessoa, com o fuso identificado em eventos (spec 13.6; `events.time_zone` IANA já existe). Nada de fuso, moeda,
  formato de data ou de telefone fixo do Brasil no código.

Porquê de negócio: é a decisão do dono sobre quem o Duora atende e onde ele roda. Porquê técnico: registrar a
mudança num só lugar evita que cada PR futuro redescubra o pressuposto antigo (UF, LGPD como única lei, PT-BR
padrão) e mantém a spec rastreável.

### Partes substituídas

| Onde | Antes | Agora |
|---|---|---|
| spec 13.6 | "Interface inicialmente em PT-BR" | Produto inteiro em PT-BR e EN-US |
| spec 12.1 | Título "requisitos brasileiros"; LGPD, Marco Civil e ECA Digital como o conjunto aplicável | Brasil como país provável (J-01); aplicabilidade da LGPD a confirmar (J-02); demais jurisdições como lacunas (data-map) |
| spec 15.1 | Região "próxima da maioria das pessoas do piloto" | Boston, EUA (decidido); latência a medir para o Brasil e para os países do lançamento |
| ADR-0022 | Interface em PT-BR (padrão) e `en` | Produto inteiro em `pt-BR` e `en-US`; idioma padrão do fallback em proposta (proposta 3) |
| ADR-0023, adendo B2, item "Jurisdição" | Transferência internacional (LGPD) só do backup | Todos os dados ficam nos EUA (fato), e o backup é uma das cópias; se há transferência (art. 33) ou coleta internacional (Res. CD/ANPD 19/2024) e qual salvaguarda é a confirmar (J-02) |
| ADR-0024 | "Adulto" sem jurisdição, ECA Digital como referência | Maioridade depende da jurisdição da pessoa (lacuna J-05); a decisão do dono sobre o ECA Digital continua valendo para o Brasil |
| model.md, `region_code` | UF em ISO 3166-2:BR | Continua implementado assim até a [proposta 1](#1-region_code-além-das-uf) ser escolhida e implementada |

O que **não** muda: a minimização (spec 12.1), a decisão do dono de não fazer análise jurídica do ECA Digital
(ADR-0024, adendo de 2026-10-16), o gate "Privacidade" da spec 23.3 (sem bases legais e fornecedores definidos, não
há coleta de dados reais) e o domínio `meetduora.com`, que não pressupõe país.

## Mudanças de código propostas

Nada abaixo foi implementado neste PR. Cada item tem uma issue; os que envolvem escolha real ficam com o rótulo
`decisão` e esperam o dono.

### 1. `region_code` além das UF

Issue: #78 (espera decisão).

Hoje: `profiles.profiles.region_code text null` com `ck_profiles_region_code` aceitando só as 27 UFs
(`20261016062338_ProfileRegion.cs`, `ProfileRecord.cs:48`), `RegionCode.All` no Core, `enum` no OpenAPI e
`REGION_CODES` com 27 rótulos no frontend (`profile-region.ts`). Uma pessoa em Boston não consegue informar região.

| Opção | Prós | Contras |
|---|---|---|
| **A. Uma coluna ISO 3166-2 de qualquer país** (`US-MA`, `BR-SP`) | Uma coluna, a forma atual dos dados já é ISO 3166-2, migração trivial | ~5.000 subdivisões: o `CHECK` com lista vira inviável e o `enum` do OpenAPI explode; não há "só o país"; subdivisão de país pequeno pode identificar demais (minimização); o ISO 3166-2 muda várias vezes por ano |
| **B. País (ISO 3166-1 alfa-2) + subdivisão opcional (ISO 3166-2), com lookup table dos países do lançamento** | "Só o país" é possível (melhor minimização); consulta por país é trivial; o conjunto habilitado é dado que o negócio altera, então tabela de referência com FK (*SQL Antipatterns* cap. 11), não `CHECK`; rótulo de país sai de `Intl.DisplayNames` no navegador, sem catálogo | Duas colunas e uma tabela de referência com seed; `CHECK` de coerência (prefixo da subdivisão = país); rótulos de subdivisão continuam no catálogo, por país habilitado |
| **C. Texto livre** ("Boston, MA") | Zero manutenção de lista | Não valida, não compara, convida a precisão demais (cidade, bairro), viola a minimização da spec 1.1 |
| **D. Manter UF e acrescentar `country_code` só para quem não é do Brasil** | Menor mudança imediata | Duas regras para o mesmo conceito; repete o problema no próximo país |

**Recomendação: B.** Colunas `country_code text null` (alfa-2) e `subdivision_code text null` (ISO 3166-2), FK
para `profiles.regions` (ou `reference.regions`, a decidir com o dono do schema em
[modules.md](../modules.md)) contendo só os países e subdivisões habilitados para o lançamento, e
`CHECK (subdivision_code is null or (country_code is not null and left(subdivision_code, 3) = country_code || '-'))`.
Migração expand/contract: (1) criar as colunas e a tabela, preencher `country_code = 'BR'` e
`subdivision_code = region_code` nas linhas existentes, leitor tolerante; (2) API e SPA passam a ler e gravar as
novas colunas; (3) remover `region_code` e o `CHECK` antigo num PR de contract. O contrato muda (API-P01/P02): campo
novo aditivo primeiro, remoção de `regionCode` depois, com `breaking-change-approved` só nesse último passo.
Custo principal: uma tabela de referência e seed por país habilitado, e a definição de quais países entram (J-01).

### 2. Conteúdo das experiências em PT-BR e EN-US

Issue: #79 (a lacuna de pareamento por idioma espera decisão).

Hoje: os descritores acessíveis das duas experiências são strings PT-BR no Core
(`ConstellationRules.cs:125-144`, `UnlikelyRoutesRules.cs:134` em diante; 16 literais com acento em cada arquivo), e o conteúdo de catálogo
(`CatalogText`) é um texto por campo, sem idioma.

Proposta: cada texto de catálogo vira um par obrigatório `pt-BR`/`en-US` dentro da mesma versão de conteúdo (a
sessão continua fixando a versão, ADR-0020); a publicação recusa versão sem os dois idiomas; a projeção de cada
participante sai no idioma dele. Alternativa descartada: uma versão de conteúdo por idioma, porque o par de uma
sessão pode ter idiomas diferentes e as duas pessoas precisam jogar exatamente o mesmo conteúdo.

Lacuna de produto (pareamento por idioma): um par com idiomas diferentes joga o mesmo cartão em idiomas diferentes, mas o chat não é
traduzido. Decidir se o idioma entra na elegibilidade do pareamento (spec 5.1) ou se o par bilíngue é aceito.

### 3. Textos da interface, dos e-mails e dos erros

Issue: #80.

Estado atual (levantado em 71381f2):

- **SPA:** todos os textos visíveis já estão nos catálogos `catalog.pt-BR.ts` e `catalog.en.ts`, com o lint
  `duora/no-literal-ui-text` barrando literal em JSX e `catalog.test.ts` exigindo as mesmas chaves. Restam: a tag
  `en` em vez de `en-US` (`locale.ts:2`); o idioma de fallback `pt-BR` (`locale.ts:6`), que, com o público principal
  fora do Brasil, deveria ser `en-US` para quem não tem `pt` nem `en` no navegador; `<html lang="pt-BR">` estático
  no `index.html:2` até o JavaScript carregar; rotas em PT-BR (`/entrar`, `/perfil`, `/seguranca`;
  [information-architecture.md](../../design/information-architecture.md)), que aparecem na barra de endereço de
  quem usa inglês; mensagens de `Error` internas em PT-BR (`chunk-load-error.ts:7`, `api-client.ts:113-152`), que
  não são exibidas hoje, mas viram texto visível se algum dia forem.
- **E-mails:** PT-BR e EN já existem (`AccountEmailCatalog.cs`, escolha por `Accept-Language`; avisos de segurança
  bilíngues desde o PR #51). Restam: a tag `en` → `en-US` (`EmailLanguage.cs:21`); o fallback
  `EmailLanguage.Default = BrazilianPortuguese` (`EmailLanguage.cs:19`) para idioma fora da lista, mesmo ponto da
  SPA. O idioma é o da requisição, não da conta: e-mails que não nascem de uma requisição (lembretes, avisos de
  evento, avisos do worker) não têm de onde tirá-lo.
- **Erros da API:** `title` do Problem Details só em PT-BR (`ApiProblems.cs:52` e `:59`, e os `Problem(...)` dos
  endpoints). A SPA decide pelo `code` e mostra texto do catálogo, mas o `title` aparece para quem integra a API e em
  logs do navegador.
- **Contato de suporte:** `Duora:SupportContact = suporte@meetduora.com` (`appsettings.json` do Web e do Worker),
  placeholder já marcado para troca em [deploy.md](../../operations/deploy.md); o endereço em português é estranho para
  o público principal. Proposta: um endereço neutro (`support@` ou `help@`) e atendimento nos dois idiomas, decidido
  junto com o canal da spec 12.5.

Proposta: trocar as tags para `pt-BR`/`en-US`; fallback `en-US` para idioma não suportado (`pt-*` continua em
`pt-BR`); `title` do Problem Details pelo `Accept-Language`, como os e-mails, ou neutro em inglês com o `code` como
contrato (recomendado: neutro em inglês, porque o `title` não é texto de interface e assim não há segundo catálogo
no backend); rotas neutras ou localizadas (recomendado: manter um conjunto só de rotas, em inglês, com redirect das
atuais, porque rota localizada duplica links de e-mail e testes).

Proposta complementar: `preferred_locale` guardado na conta (`pt-BR` ou `en-US`, `CHECK` com a lista; gravado no
cadastro a partir do idioma da requisição e alterável no perfil). Serve aos e-mails que não nascem de requisição e ao
pareamento por idioma da proposta 2 (#79); o `Accept-Language` continua valendo para quem ainda não tem conta.

### 4. Datas, fusos, números e moeda

Issue: #81.

Hoje: datas da SPA por `Intl.DateTimeFormat(locale, { dateStyle: 'short', timeStyle: 'short' })` sem o fuso no texto
(`translator.ts:38`); o fuso exibido em eventos ainda não existe na SPA; o exemplo de design usa `BRT`
([screens.md](../../design/screens.md)); os avisos de segurança por e-mail mostram o instante em UTC
([auth.md](../../security/auth.md)); não há preferência de fuso guardada; não há dinheiro no produto (cobrança fora
do piloto, spec 1.1) nem telefone.

Proposta: horário de evento sempre com o fuso do evento (`events.time_zone`) e o fuso da pessoa quando diferem
(`timeZoneName: 'short'`); preferência opcional de fuso IANA na conta, usada pelos e-mails, com UTC rotulado como
fallback; data civil (`birth_date`, se existir) nunca convertida por fuso; números por `Intl.NumberFormat`; quando
houver cobrança, dinheiro em unidade mínima (`bigint`) com moeda ISO 4217 explícita na mesma linha, nunca `BRL`
implícito; telefone, se um dia entrar, em E.164. Teste de aceite: a mesma data renderizada em `pt-BR` e `en-US` com
`America/New_York` e `America/Sao_Paulo`, no fuso do evento e no da pessoa.

### 5. Contato de suporte

Issue: #82 (espera decisão).

Ver o último item da proposta 3; fica numa issue própria porque depende da definição do canal de atendimento (spec
12.5), não só de código.

## Consequências

- Positivas: o pressuposto "público brasileiro" sai dos documentos que guiam os próximos PRs; o local do
  tratamento fica descrito como ele é (o servidor inteiro está nos EUA), com o enquadramento jurídico em aberto (J-02); o mapa de leis prováveis deixa claro o
  que bloqueia dados reais fora do Brasil.
- Negativas: o gate "Privacidade" fica maior, porque agora há várias jurisdições prováveis, e nenhuma está
  analisada; todo texto novo custa duas versões revisadas; o pareamento ganha uma pergunta de produto (pareamento por idioma).
- Trade-off: decidir país e idioma antes do código evita migração dupla (`region_code` multinacional) mas deixa o
  modelo atual (UF) em produção até a proposta 1 sair; até lá, quem não está no Brasil deixa a região em branco.

## Segurança e operação

Nenhuma superfície de ataque nova. Dados pessoais: todo o tratamento acontece nos EUA (VPS e backup), o que mexe no
mapa de dados e nas salvaguardas, não nos controles técnicos (cifra no cliente do backup, autorização por
requisição das fotos, minimização). Backup e servidor ficam no mesmo país e em provedores diferentes; um incidente
regional nos EUA pode atingir os dois, risco aceito para o piloto e coberto pelo restore ensaiado (spec 18). A
latência do Brasil até Boston precisa entrar no ensaio de capacidade (spec 15.1, 20.4).

## Compliance

- Revisão manual: todo PR que acrescenta texto voltado à pessoa usuária traz as duas versões (já garantido na SPA
  por `catalog.test.ts` e no e-mail por `AccountEmailLanguageTests`; conteúdo de experiência e erros da API ganham
  teste equivalente nas propostas 2 e 3).
- Issues das propostas 1 a 5 (#78 a #82), abertas junto com este ADR, e o PR de cada uma com testes.
- Lacunas jurídicas J-01 a J-13 em [data-map.md](../../privacy/data-map.md#jurisdições-a-confirmar): cada uma
  bloqueia dados reais da jurisdição correspondente até ter decisão (gate "Privacidade", spec 23.3).

## Levantamento dos pressupostos contrários

Feito em `origin/main` 71381f2 (2026-10-19). "Corrigido aqui" = este PR muda o documento; "proposta N" = mudança de
código acima.

| Pressuposto | Onde | Tratamento |
|---|---|---|
| Público brasileiro; LGPD, Marco Civil e ECA Digital como conjunto aplicável | `docs/product/spec.md:877` e `:881`; fichas D-PRIV-01 e D-PRIV-02 (`spec.md:1986-1987`) | Corrigido aqui (divergência registrada; ficha D-PRIV-03) |
| Direitos e bases só pela LGPD | `docs/privacy/data-map.md:5-6`, `:32-35`, `:128`, `:140-141` | Corrigido aqui (nota de jurisdição; lacunas J-xx) |
| Transferência internacional só do backup, como exceção | `docs/privacy/data-map.md:83-93`; `docs/architecture/adr/0023-…md:119-120`; `docs/operations/backup-restore.md:573-574` | Corrigido aqui |
| Fotos "no disco da VPS (Hostinger)" sem país | `docs/privacy/data-map.md:77`; `docs/architecture/adr/0010-…md:51` | Corrigido aqui (Boston, EUA) |
| Região da VPS "próxima da maioria das pessoas do piloto"; provedor e região pendentes | `docs/product/spec.md:1156`, `:1710` | Corrigido aqui (divergência registrada) |
| Interface "inicialmente em PT-BR" | `docs/product/spec.md:1051` | Corrigido aqui |
| PT-BR padrão e `en` sem região | `docs/architecture/adr/0022-…md:1`, `:40`; `frontend/src/shared/i18n/locale.ts:2`, `:6`; `frontend/index.html:2`; `backend/src/Duora.Core/Notifications/EmailLanguage.cs:19`, `:21`; `docs/security/auth.md:196-200` | Adendo no ADR-0022 e nota em auth.md; código na proposta 3 |
| Conteúdo de experiência só em PT-BR | `backend/src/Duora.Core/Experiences/Constellation/ConstellationRules.cs:125-144`; `backend/src/Duora.Core/Experiences/UnlikelyRoutes/UnlikelyRoutesRules.cs:134`; `backend/src/Duora.Core/Experiences/CatalogText.cs` | Proposta 2 |
| Títulos de erro da API só em PT-BR | `backend/src/Duora.Web/Http/ApiProblems.cs:52`, `:59` | Proposta 3 |
| `region_code` = 27 UFs | `backend/src/Duora.Core/Profiles/RegionCode.cs:7-24`; `backend/src/Duora.Infrastructure/Profiles/ProfileRecord.cs:48`; migração `20261016062338_ProfileRegion.cs:21-24`; `frontend/src/features/profile/profile-region.ts:42`; `frontend/e2e/support/fake-api.ts:46`; `docs/data/model.md:292`, `:303-317`; `docs/privacy/data-map.md:42`, `:65` | Nota em model.md e data-map; código na proposta 1 |
| Fuso do Brasil como exemplo | `docs/design/screens.md:161` (`BRT`) | Corrigido aqui (exemplo nos dois idiomas) |
| Instantes de e-mail em UTC, sem preferência de fuso | `docs/security/auth.md:394` | Proposta 4 |
| Rotas em PT-BR | `docs/design/information-architecture.md:41` | Proposta 3 |
| Contato de suporte em português e placeholder | `backend/src/Duora.Web/appsettings.json:11`; `backend/src/Duora.Worker/appsettings.json:10`; `docs/operations/deploy.md:36` | Nota em deploy.md; proposta 5 |
| "Adulto" sem jurisdição | `docs/product/spec.md:881`, `:896`; `docs/architecture/adr/0024-…md` | Divergência registrada; lacuna J-05 |
| Moeda e telefone | Nenhuma ocorrência de `BRL`, `R$` ou `+55` no código e nos docs | Regra registrada na proposta 4 |
| `meetduora.com` | `infra/caddy/Caddyfile:3`, `infra/compose/compose.prod.yml:8`, `docs/operations/deploy.md:349` | Sem conflito (`.com` não pressupõe país); nada a mudar |
