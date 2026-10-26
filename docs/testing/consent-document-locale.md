> Atualização proposta em 25/10/2026: [PR #255](https://github.com/BipoXDXD/duora/pull/255) implementa as fontes Markdown canônicas escolhidas abaixo, o leitor público e o aceite exato com SHA-256. Os documentos continuam rascunhos e o cadastro fica fechado enquanto a publicação não for aprovada. Nenhum idioma/hash histórico é inferido. Os trechos sobre rótulos com locale NULL descrevem a entrega anterior da #103. Ver [fluxo atual proposto](../identity/public-legal-documents.md).

# Idioma da evidência de consentimento — issue #103

Escopo: API-A02, spec 10.2 (consent_records), 12.1 (minimização), ADR-0019 (cadastro pendente/transação)
e ADR-0026 (idiomas). Não cria nova autorização nem altera as invariantes de elegibilidade. INV-12 permanece:
validação somente com dados sintéticos e revisão humana antes de merge/deploy.

## Persistência e compatibilidade

- Migração `ConsentDocumentLocale`: coluna `document_locale text NULL`, sem DEFAULT e sem backfill.
  CHECK permite apenas `pt-BR`/`en-US`; NULL conserva o desconhecimento. TEXT evita que VARCHAR corte espaços
  excedentes e transforme um valor inválido em um idioma válido antes do CHECK.
- Tipo, versão, idioma e instante são gravados juntos, com conta, tokens e outbox na transação já existente.
- Trigger recusa UPDATE que altere evidência, inclusive NULL -> idioma. UPDATE sem mudança é permitido.
  Não é uma política de retenção eterna: DELETE permanece para substituição de cadastro pendente (ADR-0019)
  e exclusão em cascata da conta. Conta confirmada não tem seus aceites substituídos no recadastro.
- Leitores SQL com colunas explícitas e entidade EF continuam funcionando; o campo novo é anulável.
  INSERT antigo sem a coluna continua válido (NULL). Não há leitores/exportadores de consentimento no runtime
  atual além do mapeamento; consumidores externos de SELECT * precisam tolerar coluna adicional.
- Preferência da conta não participa da escrita do idioma do consentimento, e suas alterações não reescrevem
  os registros. O índice único existente por usuário/documento/versão não muda.

## Privacidade, auditoria e limites

A informação permanece P2, nas finalidades F-01/F-08 do mapa de dados. Não se acrescentam IP, user-agent,
conteúdo documental ou idioma em logs/outbox/auditoria geral. A evidência permanece na tabela Identity;
as regras de acesso, exclusão e retenção existentes continuam valendo. Não há nova exposição em `/me`.

A issue #9 continua responsável por conteúdo, tradução, publicação e revisão jurídica. O cadastro atual
só exibe rótulos de versão: seus idiomas ficam explicitamente NULL. A API está pronta para receber o idioma
real por documento quando identificado; nunca apresenta uma preferência de UI como prova de documento visto.
Não há catálogo jurídico ou prova criptográfica de apresentação nesta mudança.

## Fonte canônica escolhida

Em 21/10/2026, o responsável informou que ainda não havia uma fonte definida e autorizou a escolha.
A fonte técnica será o próprio repositório Duora: `docs/legal/terms/<version>/<locale>.md` e
`docs/legal/privacy/<version>/<locale>.md`, com locales `pt-BR` e `en-US`. Esses caminhos são uma convenção
para a futura publicação, não documentos já existentes ou aprovados.

Cada versão publicada deve permanecer imutável; correções criam nova versão. A futura apresentação deve
usar os metadados do arquivo efetivamente exibido, inclusive em caso de fallback, e vincular o aceite a
esses metadados. Trocar documento/versão/idioma exige novo aceite. Não usar textos de terceiros, traduções
não revisadas ou o idioma da interface como substitutos. Publicação, conteúdo e aprovação seguem a #9.
Até essa entrega existir, os rótulos atuais continuam com locale NULL.

## Testes

`ConsentLocaleTests` usa PostgreSQL real/Testcontainers e os papéis migrador/aplicação do projeto:

- dois idiomas e documentos de idiomas diferentes, associados às versões correspondentes;
- preferência posterior em ambos os idiomas e recadastro de conta confirmada sem alterar evidência;
- contrato antigo, upgrade a partir da migração anterior e leitura do registro histórico sem backfill;
- leitura EF, CHECK de valores inválidos no SQL e validação de cada campo no HTTP sem efeitos colaterais;
- UPDATE proibido, inclusive preenchimento de NULL histórico, e exclusão de conta com cascata;
- falha de outbox desfaz nova conta ou substituição pendente, mantém senha/token/aceites anteriores;
- cadastros concorrentes não misturam idioma e versão de transações diferentes.

`RegisterPage.test.tsx` verifica que PT-BR/EN-US da interface não viram idioma documental sem documento.

## Ordem de atualização e rollback

Revisar SQL e executar a migração aditiva antes do novo backend; depois atualizar a SPA. O binário antigo
pode continuar usando o schema expandido e gravará NULL. Rollback operacional é voltar o binário mantendo
coluna, CHECK e evidência; **não** executar Down em produção, pois removeria evidência. O Down existe para
simetria do EF, mas não é o procedimento de rollback autorizado. Nenhuma implantação acompanha esta entrega.
