# Aprovação de licença do catálogo de localização

## CLDR 48.2 — aprovação em 2026-10-21

O responsável aprovou explicitamente ISO como referência, Unicode CLDR 48.2 como distribuição inicial e `Unicode-3.0` como licença, no contexto da [issue #78](https://github.com/BipoXDXD/duora/issues/78). Este registro e a exceção da política autorizam esse uso futuro; não incorporam o dataset, não habilitam países, não alteram dependências e não autorizam implantação.

- Identidade reservada no inventário: `duora-location-catalog-cldr-48.2`.
- Versão de origem: [release-48-2](https://github.com/unicode-org/cldr/releases/tag/release-48-2).
- Licença exata: [Unicode-3.0 da release](https://raw.githubusercontent.com/unicode-org/cldr/release-48-2/LICENSE).
- Referência dos códigos: [ISO 3166](https://www.iso.org/iso-3166-country-codes.html). A permissão de uso dos códigos não deve ser confundida com a redistribuição de publicações ou bases comerciais ISO; esta aprovação não compra uma assinatura nem autoriza copiar esses produtos.
- Recorte de produto aprovado: EUA e Brasil, 50 estados americanos + DC e 27 UFs brasileiras. Esse recorte define o futuro catálogo; o catálogo habilitado continua BR + 27 UFs.

## Proveniência conferida

Arquivos consultados diretamente na tag em 2026-10-21, sem importação dos dados para o produto:

| Arquivo na release | SHA-256 |
|---|---|
| `LICENSE` | `b49d0e9f8ead51ca8b7df6fec89cc3ae6809198b4b9d43c74216da0118f23f5b` |
| `common/subdivisions/en.xml` | `997a14da1144bb66f36a829db1783afe41f7529e33070afbe964bdd8e387b1d2` |
| `common/subdivisions/pt.xml` | `90184154e43949ea9e4d61a4570726addcad6f1cf0b12ef638f6ddb1fd7e422b` |
| `common/validity/subdivision.xml` | `93b12c9d55938266c96d44a7ccbb66800afeef4f9dd48b0dc16edfab89833d95` |

Os arquivos de rótulos consultados contêm as 78 subdivisões do recorte em inglês e português. Os 78 rótulos portugueses estão marcados como `provisional`; a aprovação de fonte/licença não substitui revisão editorial PT-BR/EN-US. O CLDR também inclui identificadores adicionais e códigos depreciados, conforme [LDML](https://www.unicode.org/reports/tr35/tr35.html#Subdivision_Codes); a futura seleção deve conferir o recorte ISO aprovado.

## Obrigações para a futura incorporação

- Distribuir os avisos completos de copyright e permissão junto ao snapshot ou à documentação associada, preservando sua procedência e licença. Não usar o nome do titular em publicidade/endosso sem autorização, conforme a licença.
- Registrar versão, fontes, hashes dos arquivos consumidos e do snapshot gerado, alterações editoriais e inventário do dataset. Arquivos adicionais, inclusive rótulos de países, precisam de proveniência e hashes próprios antes da incorporação.
- Usar a identidade acima somente para dados derivados da release aprovada. Como o verificador atual confere nome e licença, não o campo `version`, o futuro importador/inventário deve conferir versão e hashes; esta exceção não é prova automática de integridade dos dados.
- Incluir dados vendorizados na verificação de inventário/licenças da futura implementação. Os SBOMs atuais de backend/frontend não garantem essa cobertura. Não declarar o dataset como instalado nem simular cobertura apenas para consumir a exceção.
- Revisar nova release, outra licença ou ampliação do recorte em PR próprio, com autorização de produto para países novos. Não acrescentar `Unicode-3.0` à allowlist global por efeito desta exceção.

Responsáveis nominais, cadência definitiva de manutenção, revisão editorial, elegibilidade por jurisdição, implementação do catálogo e etapa contract continuam pendentes na issue #78. Esta aprovação não decide esses itens.
