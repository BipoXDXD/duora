# ADR-0025: Pipeline de fotos com ImageSharp no Worker, object storage S3 e fila na outbox

- **Status:** Accepted (2026-10-15)
- **Substituído em parte:** o storage de produção das fotos (adaptador S3 com `AWSSDK.S3` num bucket de mídia, item 5), pelo [adendo de 2026-10-16 do ADR-0010](0010-object-storage-externo-leitura-via-api.md#adendo-2026-10-16-fotos-no-disco-da-vps-com-cópia-externa) (fotos no disco da VPS)
- **Origem:** spec 2.3, 4.2, 4.3, 8.1, 8.4 (API-M01, API-M02), 8.7, 11.5, 12.3, 15.4, 16.1, 16.2 e 23.2;
  [ADR-0003](0003-monolito-modular-web-worker.md), [ADR-0008](0008-postgresql-para-jobs-e-outbox.md),
  [ADR-0010](0010-object-storage-externo-leitura-via-api.md), [ADR-0021](0021-jobs-duraveis-e-leases-no-postgresql.md);
  TM-41 a TM-44 de [threat-model.md](../../security/threat-model.md); decisões do responsável em 2026-10-15
  (issue #9)
- **Revisar quando:** a receita bruta anual passar de US$ 1 milhão (licença do ImageSharp), entrar revisão
  editorial ou triagem automática, o volume de mídia pedir URL assinada, ou a sanitização sair do Worker para o
  container `media-sanitizer`

## Contexto

O ADR-0010 decidiu object storage privado atrás de `IObjectStore` e leitura só pela API. Faltava decidir como a
foto entra, quem a decodifica, com que biblioteca, onde fica cada objeto, quem pode ler o quê e como banco e
storage ficam coerentes sem transação comum. Uma imagem é entrada hostil: arquivo poliglota, SVG renomeado,
imagem bomba (poucos bytes que decodificam em gigabytes), animação disfarçada e EXIF com GPS são os casos que a
matriz de ameaças cobre (TM-41 a TM-44) e que T-16 prova.

Alternativas consideradas:

- **Biblioteca de imagem.**
  - **SixLabors.ImageSharp 4.1.3** (escolhida). Gerenciada (sem binário nativo no container), com decoders de
    JPEG, PNG e WebP, `Identify` que lê só o header, limite de frames no decoder e alocador de memória com teto.
    Licença Six Labors Split License 1.0: Apache-2.0 para dependência direta de quem tem receita bruta anual
    abaixo de US$ 1 milhão, que é o caso; acima disso, licença comercial paga. Exige exceção em
    `license-policy.json`, feita em PR separado (o Trivy não lê a licença declarada como arquivo). Advisories: os
    sete publicados em 2026-10-07 afetam até a 4.1.1; nenhum aberto para a 4.1.3 na consulta de 2026-10-15.
  - **SkiaSharp (MIT).** Licença dentro da allowlist, mas é wrapper do Skia nativo: o binário nativo
    (`libSkiaSharp`) entra no container, os CVEs do Skia e do libwebp/libpng embutidos passam a ser nossos, e o
    limite de memória do decoder é menos controlável pelo .NET. Seria a troca se a licença do ImageSharp deixar de
    servir.
  - **Magick.NET (Apache-2.0).** ImageMagick nativo com dezenas de formatos e histórico longo de CVEs em
    decoders; desligar formatos depende de política do ImageMagick. Superfície maior que a necessária.
  - **libvips (NetVips, MIT).** Rápido e econômico, mas nativo, com mais formatos do que precisamos.
- **Storage.** Filesystem só serve a dev e teste (spec 15.4: nada de foto no disco efêmero do container); em
  produção, S3 compatível com o `AWSSDK.S3` oficial (Apache-2.0). Implementar SigV4 à mão foi descartado: é
  criptografia de autenticação caseira.
- **Fila.** Broker ou fila em memória descartados pelo ADR-0008. `ops.jobs` (ADR-0021) ainda está Proposed; a
  outbox existente, com lease e fencing, já resolve "upload concluído → processar" sem tabela nova. (Nota de
  2026-10-16: o ADR-0021 foi aceito e confirma esta escolha, porque reação a um fato vai pela outbox; a decisão
  deste ADR não muda.)
- **Transporte.** `multipart/form-data` traz parsing de partes, nome de arquivo e content-type por parte (nome do
  cliente vira tentação de caminho, TM-41). URL assinada de upload direto ao bucket tira a recepção da API, mas
  exige política de bucket que imponha tamanho e tipo, e a spec 2.3 só a admite com necessidade demonstrada.
- **Processo da sanitização.** No Web, o decoder disputaria memória e CPU com HTTP e SignalR e um crash do decoder
  derrubaria a API (ADR-0003). Num container próprio sem rede é o isolamento certo, mas é infraestrutura nova
  (imagem, rede, volume de troca) que não cabe neste marco.

## Decisão

Vamos receber a foto como corpo cru na API, guardá-la em quarentena no object storage, registrar o processamento
na outbox e sanitizá-la no Worker com ImageSharp, publicando só derivados reencodados.

1. **Transporte (API-M01).** `POST /api/v1/me/photos` com o corpo cru e `Content-Type` em `image/jpeg`,
   `image/png` ou `image/webp` (outro → `415`). `Content-Length` obrigatório (ausente, inclusive chunked → `411`)
   e de 1 byte a 5 MiB; acima disso `413` antes de ler o corpo. O teto de corpo do Kestrel (32 KiB para JSON
   comum) sobe para 5 MiB só nesta rota (`IHttpMaxRequestBodySizeFeature`), a taxa mínima de recepção é fixada
   na rota (`IHttpMinRequestBodyDataRateFeature`, contra cliente lento), e a leitura para em `Content-Length`
   bytes. Rate limit em memória por conta e por IP (hipótese de 20 envios por hora em cada um, `429` com
   `Retry-After`); como todo limitador local, reinicia com o processo e não vale entre réplicas (spec 11.5): o
   teto de pendentes no banco é a trava que vale sempre. O corpo é transmitido ao storage em streaming, com SHA-256 calculado no caminho, sem carregar o arquivo
   em memória. Nenhum nome de arquivo é aceito.
2. **Registro e idempotência.** `Idempotency-Key` obrigatório (1 a 64 caracteres `[A-Za-z0-9_-]`), gravado como
   `client_key` em `media.uploads` com `UNIQUE (owner_id, client_key)`: idempotência local da tabela, sem
   `ops.idempotency_records`. Impressão digital do pedido: tipo declarado, tamanho e SHA-256 dos bytes. Mesma
   chave e mesmos bytes → `202` com o mesmo `photoId`, sem segundo upload; mesma chave e bytes diferentes → `409
   IDEMPOTENCY_KEY_REUSED`; mesma chave com o primeiro pedido ainda transmitindo → `409
   IDEMPOTENCY_REQUEST_IN_PROGRESS` com `Retry-After`. A chave é escopada pelo dono, então outra conta com a mesma
   chave cria o próprio upload.
3. **Teto de pendentes sem write skew.** No máximo 3 uploads por conta em `Pending`, `Quarantined` ou
   `Processing` (hipótese configurável, spec 4.3 e 11.5); acima disso `429 UPLOAD_PENDING_LIMIT` com
   `Retry-After`. A serialização usa uma guarda própria do Media, `media.upload_guards (owner_id uuid PK
   REFERENCES identity.users ON DELETE CASCADE)`: na mesma transação, `INSERT ... ON CONFLICT DO NOTHING`, depois
   `SELECT ... FOR UPDATE` da linha da guarda, e só então a situação da conta é relida, os pendentes são contados e
   a reserva é inserida. Dois uploads simultâneos da mesma conta esperam um pelo outro na guarda, e o segundo conta
   o primeiro. O Media não trava nem escreve tabela de outro módulo; conta inexistente aparece como violação de FK
   (`23503`) no insert da guarda e vira "dono ausente".
4. **Máquina de estados (spec 4.2).** `Pending` (registro criado, slot reservado) → `Quarantined` (bytes no
   storage, evento na outbox, mesma transação) → `Processing` (Worker tomou a entrega, transição condicional pela
   `version`) → `Approved` ou `Rejected`; `Deleted` vem de API-M03 (fora deste marco). Falha no streaming apaga o
   `Pending` e o objeto parcial, para o cliente repetir com a mesma chave. Toda transição é `UPDATE ... WHERE id =
   $id AND state = $de AND version = $v`; a regra de transições válidas fica numa função pura no Core.
5. **Storage.** Porta `IObjectStore` no Core (`PutAsync`, `OpenReadAsync`, `DeleteAsync`; apagar o que não existe é
   sucesso). Chaves geradas pelo servidor, aleatórias, em duas áreas: `quarantine/…` para originais e
   `derivatives/…` para variantes. Adaptador de filesystem para dev e teste, que o boot recusa em `Production`;
   adaptador S3 com `AWSSDK.S3` num bucket próprio de mídia, separado do bucket de backup (ADR-0023), **sem
   Object Lock** (exclusão de foto é obrigação da spec 12.4, e Object Lock a impediria) e sem versionamento, para
   o `DELETE` apagar de fato (spec 16.2). Credenciais separadas por processo, impostas pela política do bucket:
   **Web** escreve e apaga em `quarantine/*` (o apagar é a compensação de um upload que falhou no meio) e lê
   `derivatives/`; **Worker** lê e apaga em `quarantine/` e escreve e apaga em `derivatives/`. Nenhuma credencial
   lista o bucket, e o Web não lê a quarentena. Só `NoSuchKey` conta como objeto ausente; `AccessDenied` é
   erro de configuração, registrado como Error. Timeout por chamada e retry só do SDK, com teto; o timeout do PUT
   na quarentena é pelo menos o `Media:UploadTimeout` (conferido no boot), e um timeout com o corpo ainda
   incompleto conta como falha do cliente.
6. **Fila.** O `Quarantined` grava `media.upload-quarantined` (schema 1, payload só com `uploadId`; a chave do
   objeto nunca vai ao payload) na outbox, com uma entrega ao consumidor `media-processing` e prazo de 24 h
   (= `abandon_after`). O Worker usa o mesmo protocolo da outbox de e-mail: claim com `SKIP LOCKED`, lease,
   fencing token e conclusão condicionada ao token. O efeito no storage é idempotente (mesma chave de derivado
   por upload; apagar o ausente é sucesso).
7. **Sanitização no Worker** (concorrência 1, timeout de 30 s por imagem, alocador do ImageSharp com teto de
   256 MiB), nesta ordem, parando no primeiro erro:
   1. sniff dos magic bytes do original; tipo detectado diferente do declarado → `Rejected TYPE_MISMATCH`; nada
      reconhecido (SVG, HTML, executável) → `Rejected UNSUPPORTED_FORMAT`;
   2. dimensões lidas do header (`Identify`) antes de alocar pixels; acima de 20 megapixels →
      `Rejected TOO_MANY_PIXELS`;
   3. mais de um frame → `Rejected ANIMATED`, inclusive APNG (chunk `acTL` em qualquer posição) e WebP animado
      (flag de animação do `VP8X` ou chunk `ANIM`/`ANMF`), checados pelos bytes e não só pelo decoder;
   4. decode com `Configuration` que só conhece JPEG, PNG e WebP, `MaxFrames = 1` e o alocador limitado; falha →
      `Rejected CORRUPT_IMAGE` e o Worker segue para a próxima;
   5. orientação EXIF aplicada aos pixels, perfil ICC convertido para sRGB e todos os metadados descartados (EXIF,
      XMP, IPTC, ICC);
   6. variantes `thumb` (256 px), `card` (768 px) e `full` (1600 px) no maior lado, sem ampliar, reencodadas em
      WebP; SHA-256 do original guardado no asset;
   7. original e temporários apagados depois do commit, em sucesso e em rejeição (o asset nasce com
      `original_key` nulo).
8. **Revisão.** Política `Media:Review = TechnicalOnly`: passou na sanitização, vai a `Approved`. É a única
   política implementada e só é aceita nos ambientes `Development` e `Testing` (allowlist); o boot do Web e do
   Worker a recusa em qualquer outro, inclusive `Production` e `Staging`: com ela, fotos reais não entram.
   **Pendência antes de dados reais:** revisão editorial (spec 12.3), com fila de quarentena e decisão humana.
   Sem `Media:Review` configurado, o upload fica desligado (fail closed): API-M01 responde `503
   MEDIA_UPLOADS_DISABLED`, o Worker não consome `media-processing` e o storage não é exigido no boot. É o estado
   da produção até existir uma política além da `TechnicalOnly`, e é o que deixa o deploy atual subir.
9. **Isolamento.** A sanitização roda dentro do Worker agora. **Gate obrigatório antes de dados reais:** mover o
   decode para um container `media-sanitizer` sem rede, sem credencial de storage, com limite de memória e CPU,
   recebendo os bytes e devolvendo os derivados por volume ou pipe. O processo Web nunca decodifica imagem: o
   ImageSharp só é referenciado pelo `Duora.Worker`.
10. **API-M02.** `GET /api/v1/me/photos/{photoId}/status` devolve `photoId`, `status` e `rejectionCode`. Só o
    titular: foto de outra conta e foto inexistente dão o mesmo `404`. Nenhuma resposta, log ou payload de outbox
    traz chave de objeto, bucket ou URL do storage. O original não tem rota de leitura.

Porquê técnico: o decoder hostil fica fora do processo que atende HTTP, só três formatos existem para ele, o
limite de pixels vem antes da alocação, e banco e storage ficam coerentes por máquina de estados, fencing e
efeitos idempotentes, sem transação distribuída. Porquê de negócio: perfil com foto é parte do produto, e fazê-lo
sem vazar localização nem servir arquivo não verificado protege as pessoas e o projeto; o custo da licença é zero
no porte atual.

## Consequências

- Positivas: um caminho de entrada só, sem nome de arquivo e sem multipart; quarentena sem rota de leitura;
  idempotência e teto de pendentes no banco, válidos mesmo com várias instâncias; nenhum binário nativo de imagem
  no container.
- Negativas: dependência com licença condicionada à receita, presa à versão na política (cada atualização relê a
  licença); a foto passa duas vezes pela VPS (recepção e leitura); a sanitização dentro do Worker não isola um
  bug de memória do decoder do resto do Worker até o `media-sanitizer` existir.
- Trade-off: aprovação técnica automática simplifica o M1, mas impede produção com dados reais até a revisão
  editorial; `Pending` abandonado por queda entre o streaming e o commit deixa de ocupar vaga depois de duas
  vezes o `Media:UploadTimeout` e libera a mesma chave, mas a linha e o objeto só saem com o job de limpeza
  (`abandon_after`, a implementar com API-M03 e a retenção).

## Segurança e operação

- Superfície: o Web só faz streaming e hash; o decoder roda no Worker, sem endpoint público. TM-41 (sniff, allowlist,
  reencode, chave do servidor), TM-42 (metadados descartados, chave nunca em DTO ou log), TM-43 (limites de bytes,
  pixels, frames, memória, tempo e pendentes) e TM-44 (quarentena sem rota de leitura) viram testes.
- Segredos: chaves de acesso S3 por arquivo (Compose secrets), uma por processo; nunca em log, erro ou
  `VITE_*`. O boot falha sem elas quando o adaptador é S3.
- Dados pessoais: o original com EXIF existe só na quarentena e só até o fim do processamento; o derivado não tem
  metadados. Logs levam `uploadId` e o código de rejeição, nunca a chave, o tipo declarado pelo cliente bruto nem
  bytes.
- Backup: o bucket de mídia não entra no backup do PostgreSQL; perda do bucket perde as fotos, não a integridade do
  banco (os assets ficam apontando para objetos ausentes e a leitura responde 404). Política de backup de mídia é
  pendência da operação.
- Observabilidade: entregas `media-processing` em `Failed` e `Expired`, uploads presos em `Processing` e contagem
  de rejeições por código.

## Compliance

- **T-16 (integração e unidade, M1):** SVG, HTML, executável renomeado e poliglota rejeitados; tipo declarado ≠
  detectado; 5 MiB + 1 byte → `413`; bomba 30000×30000 rejeitada sem alocar; WebP animado e APNG rejeitados;
  JPEG com GPS sintético sai sem EXIF e com a orientação aplicada; arquivo truncado vai a `Rejected` e o Worker
  processa o próximo; teto de pendentes sob concorrência (`PendingUploadsPerAccountAreCapped`); repetição com a
  mesma chave; original sem rota de leitura; `MediaResponsesNeverContainStorageKeys`. Imagens geradas no próprio
  teste, nunca fotos reais.
- **Contrato do storage:** a mesma suíte roda contra o adaptador de filesystem e contra o S3 num MinIO
  descartável (imagem `bitnamilegacy/minio` fixada por digest, a mesma do drill de backup do #19; o servidor
  MinIO é AGPL-3.0, usado só como dublê de teste em container, sem ligação nem distribuição, e não entra no SBOM).
- **Concorrência e abuso:** `PendingUploadsPerAccountAreCapped` (envios simultâneos contra a guarda, conferido
  sabotando o `FOR UPDATE`) e o rate limit por conta e por IP com `429` e `Retry-After`.
- **Arquitetura:** só o `Duora.Worker` referencia `SixLabors.*`; Core sem SDK de fornecedor (o `AWSSDK.S3` fica na
  Infrastructure).
- **Boot:** testes de startup provam que `Production` recusa o adaptador de filesystem e a política
  `TechnicalOnly`.
- **Supply chain:** exceção de licença presa à 4.1.3; o Dependabot (`nuget`, semanal) propõe atualizações, e o
  check `supply-chain` as barra até a licença ser relida.
- **Antes de dados reais (gate manual):** revisão editorial implementada; `media-sanitizer` sem rede em produção;
  bucket de mídia com as políticas de credencial acima conferidas por teste de permissão no fornecedor escolhido.

## Adendo (2026-10-16): storage de produção é o disco da VPS

Decisão do responsável (adendo do [ADR-0010](0010-object-storage-externo-leitura-via-api.md)): em produção o
`IObjectStore` é o adaptador de filesystem, num diretório do host montado no `web` e no `worker`; não há bucket de
mídia. Ficam sem efeito neste ADR: "Adaptador de filesystem para dev e teste, que o boot recusa em `Production`"
(item 5) e o teste de boot que provava essa recusa. As regras de credencial por processo do item 5 viram montagens
por área (proposta no adendo do ADR-0010). O adaptador S3 e o contrato contra o MinIO continuam, sem uso em produção.
O item "Backup" de Segurança e operação deixa de ser pendência aberta: a cópia externa de `derivatives/` (restic para
o B2) é pré-requisito para ligar o upload (gate do adendo do ADR-0010), e o gate manual "bucket de mídia com as políticas de
credencial acima" vira "montagens por área conferidas no host e backup das fotos verificado".

## Adendo (2026-10-19): em produção o storage é exigido no boot, com o upload ligado ou não

Revisão do PR #72 (issue #69). O item 8 diz que, sem `Media:Review`, "o storage não é exigido no boot". Isso continua
valendo quando `Media:Storage:Provider` também está ausente, mas não descreve a produção: o `compose.prod.yml` passa
sempre `Media__Storage__Provider: FileSystem` e `Media__Storage__FileSystemRoot` ao `web` e ao `worker`, e a validação
do adaptador de filesystem em `Production` (raiz absoluta e o marcador `.duora-media-area` em `quarantine/` e
`derivatives/`) roda sempre que o provedor é `FileSystem`, sem olhar o interruptor. **Em produção, portanto, o web não
sobe sem as duas áreas montadas e marcadas, mesmo com `DUORA_MEDIA_REVIEW` vazio.**

Alternativas consideradas:

- **Validar o storage só com o upload ligado** (backend condicional a `UploadsEnabled`). Mantém o texto do item 8, mas
  empurra a descoberta de uma montagem faltando para o dia da ativação, que é justamente o deploy de maior risco; e o
  web com o upload desligado ainda serviria derivados já gravados, que dependem da montagem.
- **Segundo interruptor no host para o provedor.** Dois valores para manter coerentes (`DUORA_MEDIA_REVIEW` e o
  provedor), e uma combinação a mais que o gate do deploy teria de entender.
- **Exigir o storage sempre, conferido também pelo deploy** (escolhida). Uma só origem de configuração, o Compose
  estático, e o erro aparece no primeiro deploy depois de reaplicar o `app_dirs`, não na ativação.

Decisão: o storage é exigido no boot de `Production` independentemente de `Media:Review`, e o `duora-deploy` confere
os mesmos marcadores (`DUORA_MEDIA_DIR` do `duora.env`, absoluto, com `{quarantine,derivatives}/.duora-media-area`)
no preflight, **antes** do pull, das migrations e de parar qualquer serviço. Sem isso, um host sem os marcadores
passava pelo deploy, migrava o banco e só então via o web cair. A recusa diz o caminho que falta e manda reaplicar o
role `app_dirs`.

Consequências: reaplicar o `app_dirs` (diretórios, marcadores e o template do `duora.env` com `DUORA_MEDIA_DIR`) é
pré-requisito de qualquer release a partir do #72, com o upload ligado ou não. O item 8 passa a ler "o storage não é
exigido no boot quando nenhum provedor está configurado"; o resto do item não muda.

Compliance: `TheFileSystemStoreRefusesAnAreaWithoutTheMountMarkerInProduction` (boot sem `Media:Review`) e
`MediaStoragePreflightTests` (preflight do deploy, inclusive a recusa pelo `main` sem rodar comando algum).

## Adendo (2026-10-19): FileSystem é o storage padrão; S3 é alternativa futura

O título e o item 5 deste ADR ainda falam em object storage S3; vale o que o adendo de 2026-10-16 acima e o
[adendo do ADR-0010](0010-object-storage-externo-leitura-via-api.md#adendo-2026-10-16-fotos-no-disco-da-vps-com-cópia-externa)
decidiram. O storage padrão das fotos, em todos os ambientes, é o `FileSystemObjectStore`, num diretório do disco da
VPS de produção (Hostinger, Boston, EUA) montado por área no `web` e no `worker`, com cópia externa dos derivados no
Backblaze B2 US East. O `S3ObjectStore` (`AWSSDK.S3`) e o contrato contra o MinIO continuam no código só como
**alternativa futura**: não há bucket de mídia nem conta na AWS, e adotar S3 em produção exige novo ADR, com as
regras de credencial do item 5 revistas para o fornecedor escolhido. O título do arquivo fica como está, para não
quebrar links.
