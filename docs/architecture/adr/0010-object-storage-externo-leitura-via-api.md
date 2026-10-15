# ADR-0010: Object storage externo e leitura privada via API

- **Status:** Accepted (2026-10-15)
- **Origem:** spec 2.3, 15.4, 16.1–16.3 e 23.1 (ADR-010)
- **Revisar quando:** egress ou latência exigirem distribuição revisada

## Contexto

Fotos de perfil são dados pessoais, passam por quarentena e moderação e precisam deixar de ser acessíveis
imediatamente após bloqueio, exclusão ou reprovação. O filesystem do container é efêmero, e a VPS pode ser
perdida inteira.

Alternativas consideradas:

- **Arquivos no disco da VPS ou do container.** Descartado: não sobrevivem à perda do host (spec 15.4).
- **Bucket público ou URL de longa duração.** Descartado: continua válido depois de um bloqueio.
- **URL assinada de curta duração.** Condicional: só se o volume exigir, com período residual registrado,
  prazo curto e revisão deste ADR (spec 16.2).

## Decisão

Vamos guardar originais em quarentena e derivados aprovados num object storage privado compatível com S3,
fora da VPS, com buckets/prefixos separados, atrás da porta `IObjectStore`. A leitura passa por
`GET /api/v1/media/{assetId}`, que revalida autorização a cada requisição e responde com `no-store`. Banco e
storage são reconciliados por máquina de estados e jobs idempotentes, sem transação compartilhada.

Porquê técnico: revogação central e persistência fora do host. Porquê de negócio: proteger dado pessoal sem
depender de URL que vaze.

## Consequências

- Positivas: bloqueio e exclusão valem na próxima leitura; a chave interna do bucket nunca sai da API.
- Negativas: tráfego e CPU de mídia passam pela VPS.
- Trade-off: objeto não referenciado após falha de commit fica órfão até o job de limpeza.

## Segurança e operação

Formatos iniciais JPEG, PNG e WebP estáticos; SVG, HTML e executáveis excluídos. Reencodar, remover EXIF e
limitar dimensões e pixels antes de processar. O fornecedor é escolhido depois, com prova de contrato
(spec 16.1, 16.3, 23.2).

## Compliance

- T-16 (upload inválido, enorme, animado ou com localização) e T-26 (asset apagado acessado por URL antiga):
  a implementar no M1.
- Teste de contrato do adaptador contra o fornecedor escolhido (a implementar quando houver fornecedor).
- Revisão rejeita URL pública de bucket em DTO ou log.
