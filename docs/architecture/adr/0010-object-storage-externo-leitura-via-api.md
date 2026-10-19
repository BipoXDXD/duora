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

## Adendo (2026-10-16): fotos no disco da VPS, com cópia externa

Decisão do responsável: as fotos (quarentena e derivados) ficam **no disco da VPS Hostinger**, num diretório do host
montado nos containers `web` e `worker`, e não num object storage. A AWS foi descartada e o backup foi para o
Backblaze B2 (adendo do ADR-0023). Isso reverte a alternativa "arquivos no disco da VPS" descartada acima; o motivo do
descarte (perda do host) passa a ser coberto por uma **cópia externa obrigatória**, não pelo lugar onde a foto mora.

O que não muda: leitura só por `GET /api/v1/media/{assetId}` com autorização a cada requisição e `no-store`; chave
interna nunca fora da API; reconciliação banco × arquivos por máquina de estados e jobs idempotentes; porta
`IObjectStore`, agora servida em produção pelo `FileSystemObjectStore` (o `S3ObjectStore` fica para o dia em que o
volume pedir).

- **Boot:** em `Production`, `Media:Storage:Provider=FileSystem` só sobe com `Media:Storage:FileSystemRoot` absoluto e
  já existente. Diretório ausente quer dizer volume não montado, e gravar ali seria o disco efêmero do container
  (spec 15.4), então o boot falha em vez de criar a pasta.
- **Separação por processo:** sem chave por processo, a separação vira montagem. Proposta para o PR que ligar o upload:
  `quarantine/` montado de leitura e escrita no `web` e no `worker`, `derivatives/` só leitura no `web` e de leitura e
  escrita no `worker`, dono `1654`, `0700`. Trade-off aceito: o `web` consegue ler a quarentena que ele mesmo grava (o
  S3 com credencial separada impedia); a quarentena continua sem rota de leitura na API (TM-44).
- **Cópia externa (pré-requisito para ligar o upload em produção):** só `derivatives/` vai para o backup; a quarentena
  é transitória (original com EXIF, 24 h no máximo) e fica de fora. Decidido pelo responsável em 2026-10-16:
  `restic` (BSD-2-Clause, pacote do Ubuntu, cifra no cliente, snapshots incrementais, `restic check --read-data`) no
  host, para o prefixo `media/` do mesmo bucket B2 com File Lock, com chave própria presa a `media/` e sem
  `deleteFiles`: backup diário, verify diário (bucket, chave, idade do snapshot, estrutura) e leitura completa semanal,
  pelas ações `media-*` do `duora-backup` e timers do role `backup`, com o mesmo alerta de falha. Operação em
  [backup-restore.md](../../operations/backup-restore.md#backup-das-fotos-restic). O repositório do pgBackRest não
  serve: ele só guarda o cluster do PostgreSQL. `rclone sync` foi descartado: espelho não é backup (uma exclusão ou
  corrupção se propaga) e a cifra seria outra camada para configurar.
- **Exclusão e restore:** apagar uma foto apaga o arquivo na VPS na hora, mas a cópia dela fica no backup até o
  snapshot sair da retenção, o lock vencer e o lifecycle limpar (decidido: 14 dias de snapshots + 21 de lock + 30 do
  lifecycle, até ~65 dias; registrado no mapa de dados e na retenção). Restaurar fotos exige aplicar as exclusões do
  banco já restaurado (tombstones, INV-11): só voltam os derivados de assets `Approved`, copiados de um staging.
- **Pacotes antigos do restic fora do lock:** o restic deduplica, então um pacote do primeiro backup serve a todos os
  snapshots seguintes; com mais de 21 dias ele está fora do File Lock e fica protegido só pelas capacidades das chaves
  (a da VPS sem `deleteFiles`; a admin nasce e morre no uso). Lacuna registrada, sem implementar: `restic check`
  independente fora da VPS com chave só de leitura de `media/`.
- **Gate no deploy:** o upload liga por `DUORA_MEDIA_REVIEW` no `duora.env`, e o `duora-deploy` recusa a release com
  ele preenchido sem o timer `duora-backup-media-verify` ativo e um `media-verify` bom de no máximo 30 h. Em
  `Production` o adaptador não cria diretório, e o boot exige o marcador `.duora-media-area` que o Ansible grava em
  cada área montada.
- **Custo e capacidade:** sem egress de leitura (a foto sai do disco local). O disco da VPS passa a ter dois
  consumidores que crescem (banco e fotos); `duora_min_free_gib` do deploy já barra disco cheio.

Gate novo: o upload de fotos **não liga em produção** (`DUORA_MEDIA_REVIEW` vazio por padrão, repassado pelo `compose.prod.yml`) sem o backup das
fotos implementado, com verify que falhe fechado e um restore de ensaio que prove a volta dos derivados. Compliance
acrescentado: `TheFileSystemStoreServesProductionFromAMountedDirectory`,
`TheFileSystemStoreRefusesAMissingDirectoryInProduction`, `TheFileSystemStoreRefusesARelativeDirectoryInProduction`,
`WebRefusesTheFileSystemPhotoStoreInProductionWithoutTheMountedDirectory`,
`TheFileSystemStoreRefusesAnAreaWithoutTheMountMarkerInProduction`, `FileSystemObjectStoreTests`,
`MediaUploadGateTests`, `MediaBackupTests`,
`MediaBackupUnitsTests` e `WriteMediaBackupRepositoryTests`. A spec 15.4 foi atualizada para esta decisão.

## Adendo (2026-10-19): localização da VPS

A VPS Hostinger onde as fotos ficam é a de produção, em **Boston, EUA** (decisão do dono, registrada no
[ADR-0026](0026-publico-idiomas-e-regiao-do-servidor.md)). As fotos de pessoas no Brasil são, portanto, tratadas
nos EUA desde o upload; a salvaguarda dessa transferência está no
[data-map.md](../../privacy/data-map.md#transferências-internacionais-registradas). Nada muda no desenho acima.
