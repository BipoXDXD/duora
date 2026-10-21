# ADR-0032: Narração natural e registros técnicos de produção

- **Status:** Accepted (2026-10-21), por instrução explícita do responsável pelo produto nesta tarefa.
- **Origem:** revisão editorial da issue #101 após o PR #123; atualiza a apresentação e os registros descritos nos ADRs 0013 e 0027.
- **Revisar quando:** exigência aplicável de divulgação, mudança de fornecedor ou mudança do escopo de voz.

## Contexto

O responsável solicitou remover referências ao processo de criação de toda a experiência e também dos registros editoriais atuais. Pediu uma voz mais natural. A orientação vale para PT-BR e EN-US e para futuras experiências, não apenas para as quatro introduções deste lote.

## Decisão

A introdução, os objetivos, as transcrições, os rótulos de narração e as descrições acessíveis tratam da experiência. Os registros editoriais não mantêm classificações declarativas de origem. Não se substitui uma classificação removida por uma alegação de gravação humana.

Mantemos os identificadores técnicos reais de produção, fornecedor, modelo, tomada, hashes, licença, consentimento quando aplicável e revisão humana. Eles permitem conferir os arquivos e seus direitos; não são texto de apresentação do produto. Não se apagam licenças nem se inventam evidências. Textos de prompts antigos substituídos deixam de ser duplicados no manifesto atual; os IDs e custos das tomadas continuam registrados, sem alterar arquivos de áudio ou atribuir a eles um roteiro diferente.

A direção de voz é adulta e conversacional: pronúncia fluente em cada idioma, pausas naturais, variação discreta de ritmo e calor sem exagero. Evitar entonação robótica, publicitária ou teatral, infantilização, sedução e urgência. Não imitar pessoa identificável. A escolha depende de escuta humana de amostras, não do nome do fornecedor ou de um parâmetro de geração. A voz do candidato anterior não é obrigatória para novas tomadas.

Transcrição, identificação de quem fala, som opcional, limites de 120 caracteres e privacidade continuam obrigatórios. O transporte operacional permanece WAV; a cue de Rotas continua pública e limitada a `intro`, com texto igual ao objetivo e descrição igual ao título.

## Consequências

- A apresentação e os documentos editoriais seguem uma redação única e neutra.
- A alteração dos roteiros exige novos arquivos e hashes antes da publicação; não altera mídia de versões em uso.
- Naturalidade desejada não significa qualidade já verificada. Nenhuma nova voz ou gravação foi produzida ou aprovada nesta decisão.
- Esta alteração de produto não conclui a revisão de aplicabilidade já registrada nos ADRs anteriores, nem certifica direitos ou publicação.
- Esta revisão modifica arquivos atuais. Não reescreve commits, PRs ou logs históricos.

## Validação

Conferir equivalência PT-BR/EN-US, correspondência entre objetivo e roteiro, limite das falas, ausência dos antigos avisos no conteúdo atual e integridade dos arquivos existentes. Executar os testes do empacotador e a validação do catálogo. Antes da produção final, ouvir e aprovar naturalidade, pronúncia e ritmo em ambos os idiomas.
