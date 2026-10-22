# Issue #101 — pronúncia de Explore com a voz original

## Alteração e limites

O responsável relatou pronúncia exagerada de **Explore** na introdução PT-BR de Constelação. A tentativa de
trocar a voz foi rejeitada. O candidato adotado conserva Sarah (`EXAVITQu4vr4xnSDxMaL`) e `eleven_v4`,
com orientação IPA somente para essa palavra. O texto visível permanece
“Explore escolhas do dia a dia. Você pode pular qualquer cartão.”

Tomada selecionada: `TmGvh4AVmGZjqZuxO751`, primeira da execução corrigida. A instrução de usar essa voz
e a concordância com o checklist foram recebidas após a comparação. Nenhum registro de aprovação final
do novo digest foi criado por inferência; evidências assistivas/direitos não foram fabricadas.

Mudanças: dois derivados novos, referência/hash/proveniência do áudio PT-BR em `constellation.json`,
manifesto, conferência da transcrição, README e registro de licenças. Os arquivos antigos são mantidos
por hash. Nenhum cartão, opção, pista, objetivo, transcrição visível, descrição acessível, ID, ordem ou regra
mudou. Rotas e os outros três áudios permanecem iguais. Não há mudança de runtime, API, schema, auth,
workflow, dependência ou política. INV-05/09/12 e API-ADM09 permanecem preservados.

## Identificação dos candidatos

| Item | SHA-256 |
|---|---|
| Constelação atual | `1b102ddd60b6ef7cb690a228a2cd57751f04c5a5fa66f9ffd8f7b932f9275cd9` |
| Constelação anterior, substituído | `15bd133630689f9121705c06d9bbb9c00eddaccf8b69e17822222fdac979b645` |
| Rotas, inalterado | `21201dc55c18179c5e0e268dbc7bef12176766a49863a08b276dcf0ec45b4d0d` |
| WAV atual | `11eb2c2d46cdb92ec7aca97d3d42c92185558bf6f3c20fe6762bf400ad5d8713` |
| AAC atual | `c05bdd924aa7a641c3a17d09aed837b0e84a77fe6a7c708f8df11b40166a8fc5` |

## Validação executada

- `python3 -m unittest discover -s tools/editorial -p 'test_*.py'`: 10 testes aprovados antes e depois.
- `git diff --check`: sem erros.
- `ffprobe`: WAV PCM signed16 e AAC-LC, ambos mono/48 kHz e 4,64 s.
- FFmpeg: normalização em duas passagens, WAV canônico gravado com `wave` e uma codificação AAC da mesma
  fonte normalizada. WAV: −16,69 LUFS / −1,50 dBTP; AAC: −16,75 LUFS / −1,38 dBTP.
- Empacotamento sem aprovação: válido e `editorialApproved: false`.
- Fonte MP3 identificada por hash `b7c085472083802ddad3e69010b6e441953976aec087538a83a1224061e249ce`.

O Scribe retornou o alias IPA inicial literalmente. O relatório preserva o retorno bruto e
`exactRawMatch: false`; somente esse prefixo é mapeado a **Explore** para comparar a transcrição visível.
O `exactMatch: true` refere-se ao texto normalizado, não à igualdade bruta. Isso não comprova naturalidade
nem substitui o aceite humano da pronúncia. Nenhum teste ou gate foi alterado para aceitar a correção.

Consumo adicional informado: 159,984 créditos na voz rejeitada e 131,9868 na correção Sarah; total do lote
2.723,7276, abaixo do teto 3.000. Nenhuma geração adicional foi feita após a escolha.

## Não executado e pendências

Sem nova suíte backend/frontend/E2E local: alteração apenas editorial/mídia; os checks remotos do PR são
execuções separadas e precisam ser conferidos. Não houve deploy, acesso a produção, publicação administrativa,
teste humano com tecnologia assistiva, comprovação documental de direitos ou aprovação vinculada ao novo digest.
Apenas `production` está registrado nos environments do GitHub consultados; nenhuma URL de homologação ou
prévia de rascunho foi confirmada. O pacote offline não demonstra isolamento efetivo entre participantes.

Reversão: reverter somente esta correção e suas referências; não alterar versões publicadas ou apagar
arquivos identificados por hash. A issue #101 permanece encerrada administrativamente. Nenhuma tentativa
de instrução não confiável foi identificada.
