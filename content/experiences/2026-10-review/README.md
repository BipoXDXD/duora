# Catálogo editorial bilíngue — candidato para revisão humana

Origem: issue #101, dependência operacional #100 atendida pelo PR #112. Este pacote usa os contratos existentes
sem mudar regras, pareamento, preferências ou a API do produto. **Revisão editorial humana pendente.**

## Conteúdo

- `constellation.json`: quatro cartões sobre pausas, uma reunião imaginária, oficinas e lembranças fictícias.
  Qualquer cartão pode ser pulado. Nenhuma resposta gera nota ou inferência sobre compatibilidade.
- `unlikely-routes.json`: uma ilha imaginária, três paradas, três opções por parada e dois conjuntos privados
  com três pistas complementares. Nenhuma rota é considerada correta; não pede endereço ou rotina real.
- `audio/`: uma introdução pública por experiência e idioma, WAV PCM canônico para a API atual e AAC-LC para
  escuta/revisão e futura adoção do formato do ADR-0027. O produto atual não importa AAC diretamente.
- `media-manifest.json`: hashes, tamanhos, duração, normalização, voz, modelo, IDs, custos e histórico de tomadas.
- `transcription-check.json`: conferência automática do candidato selecionado contra o texto. Não substitui
  a escuta humana nem comprova qualidade de sotaque, naturalidade ou acessibilidade.

Textos e traduções foram preparados com assistência de IA para este projeto, sem dados de participantes ou
reaproveitamento de texto de terceiros. As duas variantes têm os mesmos IDs e ordem. Narração de interface,
sem personagem, avatar ou identidade de participante; som não carrega regra nem pista privada.
As introduções não narram aviso sobre IA, conforme orientação do responsável nesta tarefa. A origem sintética
permanece registrada na proveniência técnica. Não há música, ambiência, barks ou áudio de pistas privadas neste
lote: o contrato operacional usa somente a cue pública `intro`.

## Voz e direitos

ElevenLabs `eleven_v4`, voz padrão Sarah (`EXAVITQu4vr4xnSDxMaL`), sem clonagem ou referência humana enviada.
Plano **Creator ativo confirmado pelo responsável** em 21/10/2026; conta identificada como a conexão OAuth,
sem inventar numeração. A tabela de [licenças](../../../docs/design/assets-licenses.md) e o manifesto registram
as fontes e os termos consultados. A disponibilidade futura da voz padrão tem limite informado pelo fornecedor;
os arquivos offline fixados por hash permanecem necessários para preservar versões publicadas.

## Empacotar para revisão

A partir da raiz do repositório:

```sh
python3 tools/editorial/package.py content/experiences/2026-10-review/constellation.json --digest --output work/constellation.review.json
python3 tools/editorial/package.py content/experiences/2026-10-review/unlikely-routes.json --digest --output work/routes.review.json
```

Crie o diretório de destino antes. O resultado usa os DTOs atuais, com bytes em Base64 e
`editorialApproved: false`. Não é um payload aprovado; a API deve recusá-lo. O empacotador confere IDs,
variantes, hashes, limite de fala, cabeçalho WAV, som não vazio e caminhos dentro do pacote. Nenhum comando
acessa fornecedores nem publica conteúdo remotamente.

## Aprovação e publicação depois da revisão

1. Conferir os textos lado a lado e ouvir os quatro arquivos. Testar a compreensão de pistas e o funcionamento
   com som desligado; validar equivalência, tradução, ritmo e pronúncia PT-BR/EN-US.
2. Registrar o nome de quem revisou, data com fuso, digest do catálogo e confirmação de direitos e escuta.
   A aprovação só vale para aqueles textos e hashes. Uma alteração exige nova aprovação e, se afetar fala,
   novo áudio nos dois idiomas antes de publicar outra versão.
3. Fornecer um arquivo de aprovação separado com os campos `reviewer`, `approvedAt`, `catalogSha256`,
   `editorialApproved: true`, `rightsVerified: true` e `audioListened: true`. Não há aprovação fictícia no pacote.
4. Reexecutar o empacotador com `--approval caminho/da-aprovacao.json --output caminho/do-payload.json`.
   Ausência ou divergência de aprovação impede gerar payload aprovado. Não confundir esta checagem local com
   assinatura criptográfica: a identidade, autorização administrativa e MFA são verificadas pelo servidor.
5. Administrador autorizado com MFA recente usa `POST /api/v1/admin/experience-versions` para Constelação ou
   `POST /api/v1/admin/route-experience-versions` para Rotas. Registrar o UUID retornado e o ambiente de destino;
   eventos usam esse UUID. Versões publicadas e assets são imutáveis. Não editar uma versão em uso.

A publicação exercitada nos testes ocorre apenas em PostgreSQL descartável, com aprovação simulada identificada
no teste. Ela comprova admissão e projeção técnica, não a aprovação editorial deste lote nem implantação.

## Checklist humana

- [ ] Tradução e equivalência semântica dos cartões, opções, exemplos e pistas.
- [ ] Nenhuma pergunta revela dados sensíveis, endereço, rotina ou inferência sobre a outra pessoa.
- [ ] Escuta PT-BR e EN-US: dicção, sotaque, pausas, naturalidade e correspondência com a transcrição.
- [ ] Primeira tomada selecionada aceita, ou escolha explícita de outra candidata com novo hash/manifesto.
- [ ] Compreensão por teclado/leitor de tela, som desligado e texto ampliado.
- [ ] Registro final de direitos, pessoa revisora, data, digest e destino de publicação.
