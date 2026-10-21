# #154 — validação de upload por adesão explícita

Decisão do responsável em 21/10/2026: ADR-0034 aceito, legado como padrão, `codes-v1` somente por
requisição e confirmação na resposta. A instrução para concluir as issues autoriza esta implementação
servidora, testes e integração após aprovação dos checks. Não autoriza ativação de upload, adesão da SPA,
retirada do legado ou deploy. A decisão de idiomas da #106 aplica inglês às novas anotações OpenAPI.

## Implementação e compatibilidade

API-M01, INV-06/INV-12, T-16/T-06/T-13. Somente as duas recusas existentes de comprimento vazio e chave
inválida selecionam representação. O helper não lê corpo, consulta dados ou mantém estado global.
Um único valor ordinal `codes-v1`, após espaços HTTP externos, escolhe os códigos. Ausência, vazio,
desconhecido, caixa distinta, duplicata e lista conservam os literais legados e sua cardinalidade.

Confirmação somente nos dois corpos com códigos. Ambas as representações combinam Vary preservando
valores, caixa e wildcard. O código principal, status, type, título inglês, detail ausente, traceId,
no-store/no-cache e ausência de Retry-After permanecem. A ordem Content-Type → comprimento → chave,
upload desativado, middleware, autorização, conta, rate limit, streaming e idempotência não foram movidos.

OpenAPI: um header de requisição opcional e dois headers de resposta opcionais no 400. Removê-los do
JSON gerado restaura estruturalmente o documento inteiro da base `41b5059`, incluindo todos os schemas,
rotas e segurança. Os tipos TypeScript foram gerados; os headers permanecem opcionais. `errors` não ganhou
enum, campo adicional ou nova cardinalidade. O diff semântico é a adesão estrita descrita neste relatório.

## Matriz de evidências locais

| Requisito | Evidência |
| --- | --- |
| Legado exato antes da implementação | 13 testes HTTP/Kestrel aprovados, incluindo os dois literais, chave Unicode/65 e schema fechado |
| Seletor e idiomas | Matriz de corpo vazio/erro composto e seis chaves inválidas, com ausência/vazio/desconhecido/case/listas/exato, em PT-BR e EN-US |
| Ausência de efeitos | Banco PostgreSQL migrado exclusivo: uploads, guards/reservas, eventos e entregas de processamento todos zero; object store vazio |
| Independência | Requisições alternadas e concorrentes com formatos distintos, sem estado global |
| Vary preexistente | HTTP com middleware sintético anterior: Accept-Encoding, wildcard, header com outra caixa e lista, sem duplicação |
| Limites e precedência | Tipo incompatível, comprimento ausente/excessivo, chave 1/64 ASCII, replay e reutilização com bytes diferentes, stream truncado |
| Segurança | Autenticação, CSRF, conta restrita/suspensa/exclusão, rate limit e upload desativado conservam recusas; sem confirmação enganosa |
| Kestrel/HTTPS | Seletor exato, duplicata/lista/case/vazio/desconhecido, ambos os campos, schema gerado/publicado; 411/413 com chave ausente nos dois formatos |
| Intermediário suportado | Caddy real da versão/digest já usado pelos testes, configuração do repositório inalterada; upstream sintético devolve legado/códigos/rollback, request header ecoado, confirmação/Vary/corpo/cache e políticas da borda preservados |
| Cliente existente | 12 testes PT-BR/EN-US: parser → erro tipado → classificador → alerta; fallback local, sem eco de texto remoto/canário e um único POST, inclusive servidor antigo/cliente opt-in |

A SPA não possui fluxo de upload ou consumidor aderente real (#152). `ApiProblemError` continua sem
transportar a confirmação; nenhum adaptador, catálogo específico ou tela foi criado. Os testes cliente
com respostas simuladas comprovam o fallback atual, não adoção ou mensagens por campo. Um futuro
consumidor terá entrega própria e exigirá confirmação exata/allowlist antes de interpretar os códigos.

A matriz inicial separou Kestrel/PostgreSQL, Caddy com upstream sintético e cliente com respostas
simuladas. A consolidação abaixo acrescenta uma prova integrada Caddy → Kestrel → PostgreSQL em ambiente
sintético. Nenhuma das camadas comprova homologação de produção ou adesão real da SPA.

## Comandos, resultados e falhas investigadas

- Build Debug, sem novas dependências; aviso de licença ImageSharp preexistente, sem alegar certificação Release.
- Baseline: `dotnet test --project backend/tests/Duora.IntegrationTests --no-build -- --filter-method '*AnEmptyBodyIsRefused' --filter-method '*AnUploadWithoutAValidIdempotencyKeyIsRefused' --filter-method '*UploadRefusalsSatisfyTheGeneratedAndPublishedErrorSchemas'`: 13 aprovados antes da mudança de runtime.
- RED: novos testes detectaram falta de Vary e retorno legado em adesão explícita. Duas expectativas iniciais de CSRF e uma de autenticação foram corrigidas para os códigos já existentes, sem alterar produto.
- Primeiro GREEN: 16 testes. Matriz ampliada: 69 aprovados. Matriz posterior: 82/84, com os dois casos 413 falhando no cliente por Broken pipe ao transmitir um corpo já recusado. Acrescentar Expect: 100-continue ao cliente de teste permite observar a recusa de headers sem transmitir esse corpo; os quatro casos 411/413 passaram, sem relaxar limite/asserção.
- `/opt/homebrew/bin/python3.14 -m unittest discover -s scripts/deploy -p 'test_caddy_security_headers.py' -v`: 29 casos, 28 aprovados e uma sonda de navegador opcional não solicitada. O novo teste Caddy executou suas três combinações. Python 3.9 do sistema não importa a sintaxe genérica preexistente do helper; nenhuma compatibilidade foi alterada para contornar isso.
- `pnpm --dir frontend test --maxWorkers=1`: 812 aprovados. A primeira execução com dois workers em carga concorrente teve sete falhas temporais/cascatas em cinco arquivos preexistentes; preservados os resultados, sem mudar timeout, teste ou implementação dessas telas.
- Tipos gerados e typecheck aprovados. Formatação/diff e checks integrais devem ser conferidos no SHA final; resultados completos e CI ficam registrados no PR.

A primeira rodada completa backend foi interrompida após detectar a expectativa nova de autenticação
incorreta; não é apresentada como aprovação. A suíte completa deve ser executada após a correção e
integração da base. Nenhum teste/scanner foi reduzido, ignorado ou posto em quarentena. Revisão independente
não encontrou P1/P2 no protocolo e pediu repetir 411/413 em Kestrel; esses quatro casos foram acrescentados
com a evidência acima. Os nove checks obrigatórios continuam condição de integração no SHA revisado.

## Consolidação dos PRs #163 e #166

O responsável autorizou explicitamente consolidar a #154 e integrar após os nove checks, sem Docker,
builds ou testes pesados no Mac de 16 GB. O #163 conserva o único helper de produção
`UploadValidationFormat.cs`. Não incorporar `PhotoValidationProblems.cs` nem reaplicar os dois patches.
A referência do #166 comparada é `5e6349e7721802183b98db1001b1a62a8f4ba00d`; as seguintes provas e
explicações foram absorvidas, sem alegar identidade byte a byte de todas as alternativas:

| Contribuição do #166 | Destino e garantia preservada |
| --- | --- |
| Helper e dois pontos de recusa | Já cobertos pelo helper canônico e pelos testes iniciais do #163; comportamento ordinal/OWS/cardinalidade, fallback e Vary equivalentes por revisão |
| OpenAPI e tipos opcionais | Contrato canônico preservado; teste adicional em `ProblemOpenApiTests` verifica documento gerado/publicado, seletor opcional e confirmação somente em 400; explicação pública em `docs/api/README.md` |
| Caddy real → backend real | `KestrelPhotoProxyTests`: mesma imagem/digest e Caddyfile, apenas endereço/certificados sintéticos; autenticação e recusas reais, confirmação/Vary/cache/headers e PostgreSQL |
| Seletores e idiomas no HTTP real | `KestrelPhotoValidationTests`: ambos os campos e idiomas, OWS, listas/duplicatas e seletores inválidos, precedência/limites, tamanhos aceitos |
| Corpo lento e outras rotas | Matriz Kestrel existente ampliada com/sem opt-in, sem relaxar limites, prazos ou asserções |
| Envelopes, chaves e replay entre formatos | `PhotoValidationCompatibilityTests`: mantém literais, campos, cardinalidade e ausência de efeitos; compara envelopes e comprova idempotência entre representações |
| Cliente antigo/novo, rollback e POST aceito | `photo-validation-compatibility.test.tsx` e fixture histórica dentro de frontend/; parser/erro/classificador/alerta reais com transporte explicitamente simulado; sem retry ou troca de chave |
| Aceite do ADR e documentação | ADR-0034 e índice já aceitos no #163; README de API, documentação de Problem Details e este relatório conservam protocolo, limites e reversão, sem segundo plano/relatório conflitante |

O helper de ausência de efeitos importado foi fortalecido: cada caso usa banco PostgreSQL exclusivo e
conta uploads, guards, eventos e entregas diretamente. Não usa INNER JOIN com uploads para provar
inexistência de eventos, pois isso esconderia eventos/entregas órfãos após a recusa. Os testes Kestrel
passaram a usar banco exclusivo por caso pelo mesmo motivo. A prova anterior do #163 com contagem direta
permanece, assim como a precedência 503 disabled contra corpo vazio, contas Suspended/DeletionPending,
autenticação/CSRF concorrendo com as novas recusas e compilação contra headers de `paths` gerados.

Antes desta consolidação, os nove checks do #163 passaram em `25b537a` ([CI 39667242692](https://github.com/BipoXDXD/duora/actions/runs/39667242692)):
2.885 backend, 812 frontend e 549 E2E na repetição sem flaky reportado. A primeira rodada E2E teve um teste
preexistente de perfil móvel aprovado no retry; esse histórico não foi ocultado nem relaxado.

**A revisão consolidada exige novo CI no novo SHA.** Nenhum teste/build/Docker novo foi executado no Mac
para esta consolidação; somente leitura, edição e verificação de diff. Os resultados do #166 não são
apresentados como execução desta nova combinação. Revisão independente e nove gates remotos devem ser
registrados no PR final antes de integração. Não fechar #154 nem declarar #166 absorvido na main antes da
verificação final e do merge único. Não houve mudança adicional de produto, dependência ou política.

## Arquivos, operação e reversão

Produção: `PhotoEndpoints.cs` e `UploadValidationFormat.cs`, apenas apresentação local das duas recusas.
Contratos: OpenAPI e schema.d.ts gerados. ADR-0034 e índice registram o aceite explícito. Testes de mídia,
helper de envio, teste Caddy e teste de compatibilidade cliente documentam a matriz; este relatório
conserva resultados e limites. Nenhuma alteração de Core, persistência, migrations, workflow, política,
configuração operacional, dependência, CORS ou limites.

Para reverter: suspender adesão nos futuros clientes controlados e, se necessário, reverter esta entrega
servidora. O teste antigo/opt-in comprova fallback sem confirmação e sem retry POST. Não limpar chaves,
repetir upload aceito ou transformar dados. Responsabilidade de release continua do proprietário/operador
autorizado; ativação e homologação operacional da #69 continuam separadas. Legado não tem prazo de retirada
presumido. Nenhum deploy, acesso a dados reais ou ativação de filtro regional/selfie/upload realizado.

Nenhuma tentativa de instrução não confiável detectada.
