# ADR-0034: Migração compatível das validações legadas de upload

- **Status:** Accepted (2026-10-21); suporte do servidor autorizado na #154, adesão e ativação separadas.
- **Origem:** [issue #141](https://github.com/BipoXDXD/duora/issues/141), [PR #128](https://github.com/BipoXDXD/duora/pull/128) e [plano integrado pelo PR #145](../../superpowers/plans/2026-10-21-issue-141-upload-validation-codes.md).
- **Revisar quando:** aparecer consumidor externo, negociação não atravessar intermediários/cliente gerado, houver proposta de retirada do legado ou mudança de idioma/fluxo de upload.
- **Relação:** complementa ADR-0018, ADR-0022/0026 e ADR-0025; não substitui decisões aceitas.

## Adendo de execução — 21/10/2026, issue #154

O responsável solicitou explicitamente a implementação opt-in em PR separado, conforme este ADR, com
testes de compatibilidade, legado padrão e sem alterar limites, segurança ou ativação. Essa instrução
aceita a decisão de protocolo aqui especificada e autoriza somente a fatia do servidor. Não autoriza
merge deste novo PR, deploy, ativação, adesão de clientes, troca de padrão ou retirada do legado.

Os pré-requisitos foram concluídos separadamente: inventário inicial #152 (projeto em fase inicial,
sem consumidores externos de upload, conforme o responsável) e fidelidade OpenAPI #153 integrada pelo
PR #160 (`41b5059`). Nenhum consumidor real de POST de foto foi encontrado na SPA. Portanto, não se cria
uma página ou um adaptador de produção para simular adoção; o cliente e o alerta existentes conservam
o fallback local. As mensagens específicas da tabela abaixo continuam reservadas ao futuro consumidor.

A implementação da #154 usa `Media/PhotoValidationProblems.cs` somente nas duas recusas de
`PhotoEndpoints.RefuseHeaders`: um pedido exato e único seleciona codes-v1; os demais mantêm os literais.
Ambos os formatos combinam Vary, e somente as recusas codificadas confirmam o formato. Regras de domínio,
ordem dos returns, middleware e configuração permanecem. O seletor é opcional nos metadados code-first.

[Relatório de implementação, testes e limites](../../testing/reports/2026-10-21-issue-154-opt-in.md).
Os testes exercitam Kestrel, PostgreSQL e a configuração versionada do Caddy em infraestrutura sintética;
os ensaios do cliente usam explicitamente transporte simulado e capturas históricas do servidor anterior.
Isso não representa homologação de cliente aderente nem rollout. A retirada/default continua exigindo
decisão própria e não recebe prazo. As evidências e frases sobre “esta entrega documental” nas seções
originais abaixo descrevem a proposta anterior à autorização, não o estado desta implementação.

## Contexto e evidências

A #141 já foi encerrada pelo PR #145 com um plano documental. Este ADR formaliza sua escolha e explicita
mensagens bilíngues, limites dos consumidores e critérios verificáveis. A conclusão do planejamento não
significa que a migração esteja implementada. Base desta conferência: `95d9da8a5552b631c49fdc87c189a313f74969cc`.
Todos os caminhos abaixo são relativos à raiz do repositório nessa revisão.

Escopo: API-M01 (`POST /api/v1/me/photos`), INV-06 (efeito único), INV-12 (limites da automação), T-16
(upload seguro), T-06 (idempotência) e T-13 (CSRF). Alteram-se apenas documentos nesta entrega.

| Evidência | Constatação e limite |
|---|---|
| `backend/src/Duora.Web/Media/PhotoEndpoints.cs`, `RefuseHeaders` | Após Content-Type, verifica Content-Length; `Empty` produz `errors.body = ["A foto está vazia."]`. Com tamanho aceito, chave ausente/inválida produz `errors["Idempotency-Key"] = ["Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'."]`. Retorna a primeira recusa; não agrega os dois erros. |
| `backend/src/Duora.Core/Media/PhotoUploadRequest.cs` | `PhotoLimits` classifica comprimento `<= 0` como Empty; HTTP válido de corpo vazio usa zero. `IdempotencyKey.TryParse` exige 1–64 caracteres ASCII `[A-Za-z0-9_-]`. Não ampliar para letras Unicode. |
| `backend/src/Duora.Web/Http/ApiProblems.cs` | `Validation` retorna 400, `VALIDATION_FAILED`, `urn:duora:problem:validation-failed`, título inglês e arrays por campo; normalização remove detail. |
| `backend/tests/Duora.IntegrationTests/Media/PhotoUploadTests.cs` | `AnEmptyBodyIsRefused` e `AnUploadWithoutAValidIdempotencyKeyIsRefused` verificam status/código e ausência de upload. A teoria da chave cobre null, vazio, espaço e caminho; falta fixar o valor exato dos campos na resposta HTTP e o limite de 65 caracteres. |
| `backend/tests/Duora.IntegrationTests/Identity/ProblemTextTests.cs` | `ValidationUsesEnglishAndPreservesFieldValues` já fixa ambos os literais no helper. Não substitui um teste do produtor HTTP. `ProblemAssert` verifica mídia, título, status, código, correlação e ausência de detail. |
| `backend/tests/Duora.IntegrationTests/Media/KestrelPhotoUploadTests.cs` | Cobertura atual de 1/5 MiB, teto de outra rota e corpo lento; os dois casos de validação precisam de cobertura HTTP real futura. |
| `docs/api/openapi.json` e `frontend/src/shared/api/schema.d.ts` | 400 referencia `HttpValidationProblemDetails`; `errors` é mapa de arrays de strings, sem enum dos valores. Idempotency-Key é obrigatório e tem limites/pattern documentados. Nenhum seletor de formato está implementado. |
| `frontend/src/shared/api/problem-details.ts` e `api-errors.ts` | Parser preserva arrays de strings de errors; `ApiProblemError` transporta o problema e usa title no Error.message. A apresentação deve continuar evitando essa mensagem bruta. |
| `frontend/src/shared/api/request-failure.ts` e `frontend/src/shared/ui/alert.tsx` | Classificador mantém status/código principal, mas descarta errors. O alerta traduz somente códigos principais em mapa local com `Object.hasOwn`, ou usa fallback. Acrescentar mensagens ao mapa não faz os novos códigos de campo chegarem à UI. |
| `frontend/src/shared/api/api-client.ts` | Aceita headers, injeta idioma/CSRF e converte falhas em erros tipados. Conserva Retry-After, mas não a futura confirmação de formato; um consumidor aderente precisa dessa informação antes de classificar. Não faz retry automático. |
| `frontend/src/shared/ui/problem-canary.test.tsx`, `frontend/e2e/problem-details.spec.ts` | Cobrem fallback local e não exibição de canários do servidor em ambos os idiomas. Não comprovam tradução por campo de upload. |

A busca por `/me/photos`, pelos dois literais e pelos novos códigos em backend/frontend/scripts confirmou:
a SPA não tem fluxo de upload; há tipos gerados e teste genérico do cliente (DELETE), sem consumidor de
POST identificado. `frontend/src/features/auth/ChangePasswordPage.tsx` já compara outro código de campo
(`SAME_AS_TEMPORARY_PASSWORD`), mas não lê body/Idempotency-Key nem depende destes literais. Não foram
encontrados outros SDKs gerados ou comparações desses literais no código
versionado pesquisado. Isso não prova ausência de scripts privados, integrações externas, aplicativos
antigos, validadores estritos ou testes de terceiros. Esses consumidores e seu tráfego são **desconhecidos**;
não foram consultados dados de produção. O histórico de decisão do ADR-0018 não serve como inventário atual
de todos os consumidores possíveis.

Limitação pública preexistente: o schema `HttpValidationProblemDetails` usa `additionalProperties: false`
e não declara `code`/`traceId`, embora o runtime os envie e o parser próprio os aceite. Um validador estrito
pode já rejeitar a resposta. Registrar uma investigação separada de fidelidade do schema; não corrigir isso
silenciosamente junto da migração. Tipos livres e um gate de OpenAPI verde não demonstram compatibilidade
semântica das strings de errors.

## Alternativas consideradas

| Estratégia | Vantagem | Custo/risco | Resultado |
|---|---|---|---|
| Trocar os valores no endpoint atual | Menor implementação e um formato só | Quebra consumidores que comparam ou exibem as strings, mesmo com schema idêntico | Rejeitada |
| Acrescentar código ao array ou extensão paralela | Permite transportar texto e código juntos | Altera cardinalidade/campos e pode quebrar validadores estritos ou exibir códigos em clientes antigos | Rejeitada como padrão global |
| Adesão explícita por requisição | Conserva o corpo legado para clientes existentes; permite retirar adesão sem migrar dados | Dois formatos, confirmação e testes de intermediários/clientes | Recomendada, conforme plano #145 |
| Novo endpoint/versão | Separação explícita dos contratos | Duplica superfície, documentação e suporte para dois valores | Reserva se a negociação se mostrar inviável |

## Decisão proposta

Vamos manter o legado como padrão e oferecer `codes-v1` somente por adesão explícita, após autorização da
implementação. Não haverá troca baseada em idioma, versão presumida do cliente ou estado global da conta.

| Campo | Valor em codes-v1 | Semântica estável |
|---|---|---|
| `errors.body[0]` | `PHOTO_BODY_EMPTY` | Comprimento declarado classificado como vazio pela regra atual; não significa stream interrompido |
| `errors["Idempotency-Key"][0]` | `IDEMPOTENCY_KEY_INVALID` | Chave ausente, vazia ou fora do domínio atual; não significa chave reutilizada com outro corpo |

Não reutilizar esses códigos para `UPLOAD_INCOMPLETE`, `IDEMPOTENCY_KEY_REUSED`, excesso de tamanho ou outro
erro. São valores de campo; o código principal continua `VALIDATION_FAILED`. Não renomear campos, agregar
erros ou mudar cardinalidade. Cada recusa continua contendo apenas o campo selecionado e um valor.

### Seleção e contrato durante a transição

- Somente no POST de foto: um único valor `Duora-Validation-Format: codes-v1` seleciona os novos valores.
  A comparação é ordinal do valor recebido após a normalização HTTP de espaços externos. Nome do header é
  insensível a caixa; o valor é sensível a caixa. Ausente, vazio, desconhecido, duplicado ou lista separada
  por vírgula mantém o legado, sem novo 400. Não aceitar valor do corpo nem combinar duplicatas em adesão.
- Nas duas recusas 400 selecionadas, confirmar `Duora-Validation-Format: codes-v1` somente quando o corpo
  contiver códigos. No legado, a confirmação fica ausente. O header não sinaliza sucesso de upload nem
  modifica outras respostas. Cliente exige confirmação exata antes de interpretar códigos de campo.
- Nos dois formatos dessas recusas, combinar `Vary: Duora-Validation-Format` com os valores existentes,
  sem duplicar ou sobrescrever `Vary: *`, mantendo a política de cache vigente
  (`Cache-Control: no-store` e `Pragma: no-cache`, em `DuoraWebApp`). Não tornar POST cacheável.
- `Accept-Language` não escolhe formato e não traduz valores da API. Manter o título público inglês,
  `type`, status/código, formato de errors, detail ausente, traceId não vazio e cabeçalhos atuais pertinentes.
  Não acrescentar Retry-After aos 400 de validação.
- Primeiro servidor com os dois formatos, depois clientes aderentes. Um cliente aderente contra servidor
  antigo recebe o legado e usa fallback local. Nunca repetir POST apenas para descobrir o formato.

A negociação é apenas apresentação de duas recusas existentes. Upload desativado continua prevalecendo no
handler. Content-Type → comprimento → chave mantém sua ordem. As barreiras de middleware e as verificações
de conta/rate limit permanecem nas posições atuais; não mover validação para alterar sua precedência.

### Mensagens localizadas e consumo futuro

As frases abaixo são **texto proposto de catálogo local**, sem alteração nos catálogos executáveis agora.
Na API, codes-v1 é igual em pt-BR/en-US e o legado conserva seus literais em português nos dois idiomas.

| Contexto/chave proposta | PT-BR | EN-US |
|---|---|---|
| `PHOTO_BODY_EMPTY` → `photos.emptyBody` | A foto está vazia. Escolha outra foto. | The photo is empty. Choose another photo. |
| `IDEMPOTENCY_KEY_INVALID` → `photos.invalidUploadKey` | Não foi possível validar o envio da foto. Tente novamente. | We couldn't validate the photo upload. Please try again. |
| Resumo conhecido de `VALIDATION_FAILED` no futuro fluxo → `photos.validationFailed` | Não foi possível enviar a foto. Confira o arquivo e tente novamente. | The photo could not be uploaded. Check the file and try again. |
| Desconhecido/legado/sem confirmação → `common.unexpectedError` (já existente) | Algo deu errado do nosso lado. Tente de novo em instantes. | Something went wrong on our side. Please try again in a moment. |

Um futuro adaptador de upload deve conservar o Problem Details e a confirmação de formato até selecionar,
por propriedade própria, uma lista local de pares `(campo, código)` conhecidos. Exigir HTTP 400, código
principal `VALIDATION_FAILED`, confirmação `codes-v1`, um campo esperado e array unitário conhecido.
Qualquer combinação desconhecida, malformada, legada ou sem confirmação usa o fallback genérico, sem tentar
traduzir texto remoto ou adivinhar pelo conteúdo. Não usar title/detail/errors/Error.message como texto
visível, chave de tradução dinâmica ou HTML. O resumo conhecido só se aplica após essa validação.

A solução pode ser local ao futuro consumidor; não ampliar o classificador compartilhado só para criar
uma tela inexistente. Até haver esse fluxo, o alerta atual continua exibindo o fallback local. Não prometer
mensagens específicas por campo como comportamento atual. Um teste de componente pode provar o adaptador
sem criar uma página; E2E de upload depende da existência de um fluxo real.

## Compliance: testes de contrato a implementar depois

Todos os cenários abaixo são **especificações futuras**, não testes executados nesta entrega.

Para cada recusa alvo: autenticação/CSRF válidos e upload habilitado; conferir HTTP 400 e status do corpo
400, `application/problem+json`, código principal `VALIDATION_FAILED`, type
`urn:duora:problem:validation-failed`, título `The request contains invalid fields.`, detail ausente,
traceId não vazio, um campo com array unitário e nenhum campo extra em errors. Comparar corpos ignorando
somente traceId. Comparar presença/valores dos cabeçalhos estáveis ao baseline, ressalvados Vary e confirmação
explicitamente previstos. Preservar cabeçalhos de segurança/cache/correlação; Retry-After continua ausente.

| Caso | Sem adesão (inclui seletor inválido) | Com codes-v1 | Verificação adicional |
|---|---|---|---|
| Content-Length zero, tipo aceito e chave válida | `body: ["A foto está vazia."]` | `body: ["PHOTO_BODY_EMPTY"]` | Nenhum upload, reserva, objeto ou evento/entrega de processamento criado |
| Chave ausente, vazia, 65 caracteres, espaço, barra ou Unicode; bytes/tipo/comprimento válidos | Literal legado exato de Idempotency-Key | `Idempotency-Key: ["IDEMPOTENCY_KEY_INVALID"]` | Mesma ausência de efeitos; não registrar entrada ou chave bruta |
| Corpo vazio e chave inválida | Só errors.body legado | Só errors.body com código | Não agregar errors.Idempotency-Key |
| Chaves com 1 e 64 caracteres ASCII permitidos, incluindo hífen/sublinhado | Aceita a validação da chave | Mesma decisão | Usar fixture válida; demais recusas não viram IDEMPOTENCY_KEY_INVALID |
| Tipo incompatível + ambos os erros | 415 / UNSUPPORTED_MEDIA_TYPE | Igual | Preservar precedência |
| Sem Content-Length ou acima do limite atual + chave inválida | 411 / LENGTH_REQUIRED ou 413 / PAYLOAD_TOO_LARGE | Igual | Não alterar teto de upload nem limite global de outras rotas |
| Stream interrompido ou chave reutilizada com bytes diferentes | Comportamento/código anterior | Igual | Não confundir com os dois novos códigos |
| Upload desativado, conta sem acesso, CSRF inválido, rate limit | Recusa anterior conforme caso/precedência | Igual | Nenhum bypass, efeito ou confirmação enganosa |

Executar matriz de seletor: ausência, vazio, valor desconhecido, `CODES-V1`, duplicado, lista por vírgula e
exato; repetir os casos de comprimento e headers em Kestrel. Alternar requisições e executar concorrência
com formatos distintos para provar independência. Validar confirmação/Vary, inclusive Vary preexistente e
`*`, e preservação através de intermediários suportados antes de liberar adesão.

Testes de consumidor e apresentação:

1. Servidor novo/cliente antigo, novo/aderente e antigo/aderente: aceitação/parsing e fallback sem retry,
   sem desativar verificações estritas dos consumidores suportados. Gerar tipos e compilar o cliente; não
   inferir suporte só porque `errors` admite string. O futuro header precisa ser opcional no OpenAPI.
2. Parser → erro tipado → adaptador local → apresentação: ambos os códigos/pares conhecidos nos dois
   idiomas devem produzir os literais da tabela. Trocar o idioma deve atualizar texto sem outra requisição.
   Validar que a confirmação sobrevive à conversão de erro; ela hoje não é transportada.
3. Sem confirmação, confirmação inválida, legado, código principal desconhecido, campo/código desconhecido,
   arrays múltiplos/vazios ou dados malformados: fallback exato nos dois idiomas. Testar `constructor`,
   `__proto__`, `toString` e canários em title/detail/errors; nenhum eco ou execução no DOM.
4. Catálogos com mesmas chaves/argumentos; testes do alerta compartilhado preservados. Em fluxos reais,
   manter html lang, acessibilidade e axe. Nenhuma tela de upload será criada só para cobrir este ADR.

## Compatibilidade, adoção e retirada

Compatibilidade exige corpo legado idêntico para cliente sem adesão, exceto correlação variável; mesma
semântica de autenticação, CSRF, rate limit, validação, streaming, limites, idempotência e efeitos. Os únicos
acréscimos previstos são os headers descritos. Se consumidor suportado rejeitar esses headers ou a
negociação falhar, não liberar adesão: revisar este ADR e considerar uma versão separada.

Antes de propor retirada, registrar inventário de clientes/versões suportados, responsável por consumidor,
confirmação de adoção, matriz de compatibilidade aprovada, comunicação e janela de suporte aprovadas pelo
responsável técnico. Possíveis métricas serão agregadas por formato, sem IP, identidade, corpo, foto ou
chave de idempotência; definir sua coleta em trabalho separado. Ausência de tráfego não comprova ausência
de dependência externa. Enquanto houver consumidores desconhecidos, manter legado por padrão, sem prazo
presumido. Alterar padrão ou remover legado exige nova decisão de mudança semântica e aprovação explícita,
mesmo se o gate de schema não detectar quebra. Não encerrar suporte automaticamente por data ou percentual.

## Segurança, operação e reversão

Não mudar uploads habilitados/desabilitados, limites/tipos, domínio da chave, policies, CORS, autenticação,
CSRF, rate limit, persistência ou infraestrutura. Outra origem exige revisão própria; não ampliar CORS para
fazer o seletor funcionar. Observabilidade não pode capturar a chave ou conteúdo; usar apenas agregados
aprovados e correlação existente. Nenhum dado real foi necessário nesta análise.

Reverter adesão se ocorrer quebra de consumidor suportado, formato sem confirmação correta, erro de
precedência, vazamento de texto remoto ou efeito inesperado. Primeiro suspender adesão nos clientes sob
controle; depois reverter a implementação do servidor se necessário. Cliente aderente deve funcionar com
servidor antigo mesmo quando não puder ser atualizado imediatamente: sem confirmação, fallback local.
Preservar chaves e registros existentes, sem limpar idempotência nem reenviar upload aceito. Não há migração
de dados a desfazer. Ensaiar esse cenário antes da release e anexar evidência à revisão.

## Consequências e trabalho posterior

- Positivas: códigos independentes de idioma, legado preservado e reversão sem transformar dados.
- Negativas: dois formatos e cobertura de negociação/consumidores; dependências externas continuam abertas.
- Trade-off: pequeno custo de protocolo por compatibilidade semântica; versionar endpoint se esse custo
  não puder ser sustentado pelos clientes/intermediários suportados.

A implementação requer autorização posterior explícita que cite este ADR e o plano integrado. Ordem:

1. Fixar baseline HTTP dos literais/precedência/efeitos em PhotoUploadTests e KestrelPhotoUploadTests,
   reaproveitando ProblemAssert; PostgreSQL real para efeitos transacionais.
2. Implementar seleção local nas duas recusas, confirmação/Vary e testes da matriz; preservar Core e
   regras de recebimento. Atualizar anotações do endpoint e gerar OpenAPI/tipos; revisar diff semântico.
3. Quando existir cliente aderente, conservar confirmação no transporte e implementar adaptador local e
   catálogos/testes bilíngues; não criar upload na SPA como efeito colateral.
4. Resolver em trabalho separado o inventário externo e a fidelidade de extensões no schema. Aprovar
   adoção/release e, somente após critérios próprios, propor retirada ou mudança do padrão.

Executar checks exigidos por AGENTS.md/CI no SHA da implementação futura, incluindo contrato, segurança e
suítes completas. Aprovação deste ADR não autoriza implementação, merge de código, ativação de upload ou
deploy. A presente entrega é exclusivamente documental; não comprova esses testes futuros nem homologação.
