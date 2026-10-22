# Catálogo BR + US

Implementação solicitada pelo titular em 22/10/2026 para a #78. A release passa a aceitar Brasil e Estados Unidos, com 27 UFs e 50 estados + DC. País e subdivisão permanecem opcionais. PR, GU, VI, AS, MP e UM não são habilitados.

A migração `20261022120000_UnitedStatesLocations` acrescenta somente país e subdivisões de referência. Não reescreve perfis ou preferências. `regionCode` legado continua exclusivamente BR; perfis US projetam null nesse campo. ETag, guardas e FKs permanecem. A API, os seletores e a preferência regional aceitam US e a combinação BR/US; sugestão não troca escolha manual e não inventa subdivisão.

Fonte aprovada: CLDR 48.2, licença Unicode-3.0. O subconjunto, inventário de hashes e aviso ficam em `content/location/cldr-48.2`. Os quatro arquivos de origem foram conferidos contra os hashes previamente aprovados em `docs/security/catalog-licenses.md`. Os rótulos US são os da fonte; os rótulos BR existentes não mudam. Os rótulos portugueses da fonte permanecem provisórios para revisão editorial do titular. O aviso também acompanha a SPA em `/licenses/CLDR-48.2.txt`. O teste de supply chain confere inventário, hash, subconjunto e licença usando a política existente; não amplia a allowlist.

Implantar servidor e migração antes da SPA, após CI e revisão humana da migração. Preservar o schema em rollback; pausar edições se voltar a binário sem US. Down é recusado para preservar referências. Nenhuma ativação regional V3, detecção MMDB, atualização, SMTP ou política de idade é introduzida neste PR. Essas integrações operacionais têm entrega separada; habilitar US no catálogo não afirma que elas estejam prontas.

Validação local: regressões de formulário, troca de país, preferências US/BR+US, tipos TypeScript e integridade/licença do catálogo. Backend/PostgreSQL, E2E, regeneração OpenAPI pelo .NET e imagens devem ser comprovados no CI remoto. A preparação local não é evidência de implantação.
