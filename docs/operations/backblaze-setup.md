# Bucket e chaves no Backblaze B2 (US East)

Runbook para o **responsável** criar, uma vez, o bucket de backup do Duora no Backblaze B2 e as application keys
restritas. Nenhum agente roda estes comandos: eles exigem o login da conta B2, que nunca entra na VPS nem no CI.
Decisão do provedor: Backblaze B2, região US East (responsável, 2026-10-16; adendo do
[ADR-0023](../architecture/adr/0023-topologia-do-backup-repository-host-e-object-lock.md)). As fotos ficam no disco
da VPS (adendo do [ADR-0010](../architecture/adr/0010-object-storage-externo-leitura-via-api.md)) e não têm bucket de
leitura; os derivados vão para o prefixo `media/` deste bucket com restic
([backup-restore.md](backup-restore.md#backup-das-fotos-restic)).

| Item | Valor |
|---|---|
| Bucket | Um só, privado (`allPrivate`), **próprio do Duora**: a conta B2 é compartilhada com outro projeto |
| Proteção | File Lock (Object Lock) ligado na criação, retenção padrão `compliance` de 21 dias, lifecycle de versões ocultas |
| Prefixos | `duora/` (repositório do pgBackRest, `repo1-path=/duora`); `media/` (repositório restic das fotos) |
| Cifra | SSE-B2 por padrão, além da cifra do pgBackRest no cliente (`aes-256-cbc`): a Backblaze só vê blocos cifrados |
| Endpoint | `s3.us-east-005.backblazeb2.com` é o provável; **confirmar no bucket** (passo 1) |

O B2 não tem política de bucket nem IAM: tudo o que restringe o acesso mora nas **application keys** (bucket, prefixo
de nome e capacidades). Por isso o `duora-backup verify` confere a chave da VPS a cada execução (passo 6).

## 0. Pré-requisitos (máquina do responsável)

- CLI oficial do B2, versão 4 (`pipx install b2` ou `brew install b2-tools`). Confira com `b2 version`; os comandos
  abaixo seguem a documentação **estável** do CLI 4 (por exemplo, `b2 file hide <bucket> <arquivo>`; a forma
  `b2 file hide b2://...` só existe no ramo master). Se um comando mudar de forma, confira em `b2 <comando> --help`.
- Login da conta com **2FA ligado** na web da Backblaze. Para o CLI, use uma chave temporária criada na web
  (*Application Keys → Add a New Application Key*, todas as capacidades, sem restrição de bucket, com validade
  curta) e apague-a no fim. A master key não serve para a API S3 e não deve ficar em disco.
- Cofre do responsável (Bitwarden) com uma pasta `Duora / backup`.

```sh
# No cofre, nunca no histórico: a chave temporária de administração entra por variável de ambiente.
read -r B2_APPLICATION_KEY_ID; read -rs B2_APPLICATION_KEY; export B2_APPLICATION_KEY_ID B2_APPLICATION_KEY
# Nome sem ponto e sem dado pessoal; o namespace dos buckets é global. Sugestão: duora-<ambiente>-backup-<sufixo>,
# com o sufixo de "openssl rand -hex 3". O mesmo nome vai em duora_b2_backup_bucket no inventário.
BUCKET=duora-prod-backup-<sufixo>
```

## 1. Bucket com File Lock

O File Lock **só liga na criação** e não desliga depois.

```sh
b2 bucket create --file-lock-enabled --default-server-side-encryption SSE-B2 \
  --lifecycle-rule '{"fileNamePrefix": "duora/", "daysFromHidingToDeleting": 30, "daysFromUploadingToHiding": null}' \
  --lifecycle-rule '{"fileNamePrefix": "media/", "daysFromHidingToDeleting": 30, "daysFromUploadingToHiding": null}' \
  "$BUCKET" allPrivate
b2 bucket update --default-retention-mode compliance --default-retention-period "21 days" "$BUCKET"
b2 bucket get "$BUCKET"
```

- **Retenção `compliance` de 21 dias** (a janela máxima do repositório): ninguém, nem o dono da conta, apaga ou
  encurta a retenção de uma versão antes do prazo. **Não use um prazo maior por engano**: o bucket não pode ser
  esvaziado até cada versão vencer. O `duora-backup verify` recusa `governance` e menos de 21 dias.
- **Lifecycle**: uma versão que deixou de ser a corrente (sobrescrita, ou escondida pelo hide marker que o `expire`
  do pgBackRest grava) é apagada 30 dias depois (21 do lock + margem). `daysFromUploadingToHiding: null`: nada é
  escondido sozinho. O B2 adia a remoção de uma versão ainda travada até o lock vencer.
- **Versionamento**: no B2 todo bucket guarda versões; o `GET ?versioning` responde `Enabled`, e o verify confere.
- **Endpoint**: anote o campo *Endpoint* do bucket na página *Buckets* da web (também aparece como `s3ApiUrl` em
  `b2 account get`). É o valor de `--endpoint` no passo 4 e de `duora_b2_s3_endpoint` no inventário. Se não for
  `s3.us-east-005.backblazeb2.com`, use o que aparecer: o número da região é da conta, não do Duora.

### Caps & Alerts da conta

Na web, *Caps & Alerts*: defina um teto diário para armazenamento, download e transações das classes B e C, com
alerta por e-mail a 75% e 100%. A conta é compartilhada; o teto vale para os dois projetos, então dimensione pelo
consumo somado. O teto **bloqueia** a operação quando é atingido (o backup falharia e o `duora-backup verify`
alertaria): use-o como freio de custo com folga, não como orçamento justo. Sugestão inicial: download de 40 GB/dia
(o `verify-repo-full` de quarta lê ~29 GB num dia só) e alerta de armazenamento em 100 GB.

## 2. Outras chaves da conta (conta compartilhada)

A conta tem chaves do outro projeto. Uma chave **sem restrição de bucket** e com `deleteFiles` apaga versões do
Duora com mais de 21 dias (fora do lock) e os hide markers que o restore depois de um ataque precisa remover.

```sh
b2 key list --long
```

Toda chave que não é do Duora precisa mostrar o bucket do outro projeto na coluna de restrição. Uma chave sem
restrição (`-` no bucket) é a primeira a corrigir: crie outra presa ao bucket certo, troque no outro projeto e apague
a antiga.

## 3. Chaves

Capacidades mínimas por uso. O `b2 key create` imprime o key ID e a application key **uma única vez**: copie as duas
direto para o cofre, sem gravar em arquivo nem colar em chat, issue ou commit.

| Chave | Onde fica | Prefixo | Capacidades | Para quê |
|---|---|---|---|---|
| `duora-backup` | VPS (`pgbackrest_repo.conf`, passo 4) e cofre | `duora/` | `listFiles,readFiles,writeFiles,readBuckets,readBucketRetentions` | pgBackRest (backup, WAL, `expire`) e `duora-backup verify` |
| `duora-backup-verify` | só no cofre | `duora/` | `listFiles,readFiles,readBuckets,readBucketRetentions` | Conferência independente fora da VPS (passo 6) e restore de ensaio fora da VPS |
| `duora-media-backup` | VPS (`restic_media_credentials`) e cofre | `media/` | `listBuckets,listFiles,readFiles,writeFiles,readBuckets,readBucketRetentions` | restic das fotos (`listBuckets`: o `restic init` confere que o bucket existe) e `media-verify` |
| `duora-backup-admin` | **nunca guardada**: criada na hora, com `--duration`, e apagada no fim | nenhum | `listFiles,readFiles,writeFiles,deleteFiles,readBuckets,writeBuckets,readBucketRetentions,readFileRetentions` | Remover hide markers depois de um ataque, mudar lifecycle |

```sh
b2 key create --bucket "$BUCKET" --name-prefix duora/ duora-backup \
  listFiles,readFiles,writeFiles,readBuckets,readBucketRetentions
b2 key create --bucket "$BUCKET" --name-prefix duora/ duora-backup-verify \
  listFiles,readFiles,readBuckets,readBucketRetentions
b2 key create --bucket "$BUCKET" --name-prefix media/ duora-media-backup \
  listBuckets,listFiles,readFiles,writeFiles,readBuckets,readBucketRetentions
```

A `duora-media-backup` vai para a VPS pelo `duora-secrets write-media-backup-repo`, junto com a senha do repositório
restic ([backup-restore.md](backup-restore.md#primeira-configuração-das-fotos)).

Por que essas capacidades (tabela de operações S3 × capacidades da Backblaze):

- `writeFiles` cobre `PutObject`, o multipart e o `DeleteObject` **sem** `versionId`, que no bucket versionado só
  grava um hide marker. É o que o `expire` do pgBackRest faz.
- **Sem `deleteFiles`**: é ela que apaga uma versão (`DeleteObject` com `versionId`). Sem ela, nem um atacante com a
  chave da VPS apaga o histórico; com o lock, nem quem tiver `deleteFiles` apaga os últimos 21 dias.
- `readBuckets` (`GetBucketVersioning`) e `readBucketRetentions` (`GetObjectLockConfiguration`): o verify lê os dois.
- Nenhuma capacidade de retenção, legal hold, bucket, chaves, compartilhamento (`shareFiles`) ou `bypassGovernance`.
  O verify reprova qualquer capacidade fora da lista, inclusive uma que a Backblaze criar no futuro.

**Chave administrativa só na hora** (decidido em 2026-10-16): o B2 não exige 2FA para usar uma application key, então
uma chave com `deleteFiles` guardada seria uma credencial de apagar sem segundo fator. Ela **nunca** é guardada (nem
no cofre): nasce do login com 2FA, com `--duration` curta, para uma tarefa, e é apagada ao fim dela.

```sh
b2 key create --bucket "$BUCKET" --duration 3600 duora-backup-admin \
  listFiles,readFiles,writeFiles,deleteFiles,readBuckets,writeBuckets,readBucketRetentions,readFileRetentions
# ... uso ...
b2 key delete <keyID da duora-backup-admin>
```

Ao terminar o passo 3, apague a chave temporária de administração do passo 0 (`b2 key delete`, ou na web), rode
`unset B2_APPLICATION_KEY_ID B2_APPLICATION_KEY` e `b2 account clear` (apaga o cache local de autorização do CLI,
que guarda um token válido por até 24 h).

## 4. Gravar a chave do backup na VPS

Na VPS, como o usuário administrativo. O `read -s` não ecoa o segredo nem o deixa no histórico; o `duora-secrets`
recusa valor com espaço ou quebra de linha (o arquivo é INI) e nunca repete o segredo num erro.

```sh
( IFS= read -r -p 'keyID: ' key; IFS= read -r -s -p 'applicationKey: ' secret; echo >&2
  printf '%s\n%s\n' "$key" "$secret" ) \
  | sudo duora-secrets write-backup-repo /etc/duora/secrets --bucket <BUCKET> \
      --endpoint s3.us-east-005.backblazeb2.com   # o endpoint anotado no passo 1
```

Grava `/etc/duora/secrets/pgbackrest_repo.conf` (0400, dono 999) com `repo1-s3-endpoint` (só o host, sem
`https://`), `repo1-s3-region` tirado do endpoint (`us-east-005`) e `repo1-s3-uri-style=host`. Se o arquivo já
existir, o comando recusa; na rotação, use `--replace` (troca por rename atômico). A senha de cifra
(`pgbackrest_cipher.conf`) continua como em
[backup-restore.md](backup-restore.md#segredos-do-repositório-operador-uma-vez); guarde-a no mesmo cofre.

| Segredo | Onde fica |
|---|---|
| `duora-backup` (key ID e application key) | VPS (`pgbackrest_repo.conf`) e cofre |
| `duora-backup-verify` | só no cofre |
| `duora-media-backup` (key ID e application key) | VPS (`restic_media_credentials`) e cofre |
| Senha do repositório restic das fotos | VPS (`restic_media_password`) e cofre, com cópia de emergência fora dele |
| `duora-backup-admin` | em lugar nenhum: nasce e morre no uso, nunca é guardada |
| Senha de cifra do pgBackRest | VPS (`pgbackrest_cipher.conf`) e cofre, com cópia de emergência fora dele |
| Login da conta B2 | cofre, com 2FA |

## 5. Filtro de saída

O container `backup` só sai para os CIDRs de `duora_backup_s3_cidrs`. A Backblaze não publica uma lista
legível por máquina; o artigo *Backblaze IP Address List* da central de ajuda lista `206.190.208.0/21`,
`104.153.232.0/21`, `149.137.128.0/20` e `45.11.36.0/22` (e avisa que muda). Antes de usar, confira **na VPS** que o
endpoint S3 e a API nativa (que o verify consulta) caem dentro deles:

```sh
getent ahostsv4 s3.us-east-005.backblazeb2.com api.backblazeb2.com | awk '{print $1}' | sort -u
```

Um endereço fora dos blocos: acrescente o bloco do artigo atualizado (ou o prefixo do AS40401 da Backblaze) e rode o
playbook (role `backup`). Limite: o filtro libera **toda** a Backblaze, inclusive buckets de terceiros; a defesa
contra exfiltração para um bucket do atacante continua sendo o pgBackRest cifrar no cliente.

Depois, a sequência de [backup-restore.md](backup-restore.md#primeira-configuração) (stanza, full e verify).

## 6. Validar

**No servidor** (prova positiva; o deploy só segue com ela):

```sh
sudo duora-backup verify
```

Com endpoint `*.backblazeb2.com`, ele chama o `b2_authorize_account` com a chave da VPS e falha se ela tiver
capacidade fora da lista do passo 3, faltar uma das necessárias, não estiver presa só a este bucket e ao prefixo
`duora/`, ou se o `s3ApiUrl` da conta não for o endpoint gravado (número da região errado). Depois assina
`GET ?versioning` e `GET ?object-lock` e falha sem versionamento `Enabled` e lock `COMPLIANCE` de pelo menos 21 dias.
Uma resposta 401 quer dizer chave errada, revogada ou expirada.

**Na máquina do responsável** (provas negativas), com uma **chave temporária** criada só para o teste, com as mesmas
restrições da `duora-backup` e validade de 1 h (a chave da VPS nunca sai da VPS):

```sh
b2 key create --bucket "$BUCKET" --name-prefix duora/ --duration 3600 duora-teste-canario \
  listFiles,readFiles,writeFiles,readBuckets,readBucketRetentions      # imprime keyID e applicationKey
(
  read -r B2_APPLICATION_KEY_ID; read -rs B2_APPLICATION_KEY; export B2_APPLICATION_KEY_ID B2_APPLICATION_KEY
  echo canario > /tmp/canario.txt
  b2 file upload "$BUCKET" /tmp/canario.txt duora/canary/lock-test     # esperado: sucesso
  b2 rm b2://"$BUCKET"/duora/canary/lock-test                         # esperado: recusa (sem deleteFiles)
  b2 ls b2://"$BUCKET"/media/                                         # esperado: recusa (fora do prefixo)
  b2 file hide "$BUCKET" duora/canary/lock-test                       # esperado: sucesso (só hide marker)
  rm /tmp/canario.txt
)
b2 account clear
```

Com a `duora-backup-admin` criada só para isso (passo 3):

1. `b2 ls --versions --long b2://"$BUCKET"/duora/canary/` mostra duas linhas: o upload e o hide marker.
2. `b2 rm b2id://<fileId do upload>` → recusa pelo File Lock `compliance`, mesmo com `deleteFiles`.
3. `b2 rm b2id://<fileId do hide marker>` → sucesso: é o caminho da recuperação depois de uma exclusão (o canário volta
   a ser a versão corrente).
4. `b2 file hide "$BUCKET" duora/canary/lock-test` de novo, para o lifecycle apagar o canário 30 dias depois de o
   lock vencer. Sem esse hide final o canário ficaria para sempre: o lifecycle só apaga versão oculta.
5. Apague a chave admin e a de teste (`b2 key delete`) e rode `b2 account clear`.

**Em homologação, antes da produção** (prova do caminho S3 que o pgBackRest e o restic usam com chave sem
`deleteFiles`). O `expire` só chama o `DeleteObjects` quando há o que expirar, então force: dois fulls e um `expire`
com retenção de **um** full só naquele comando (apaga o full mais antigo e o WAL dele; em homolog, e recuperável pelos
hide markers):

```sh
sudo duora-backup full && sudo duora-backup full                               # dois fulls (na VPS de homolog)
b2 ls --versions --recursive --json b2://"$BUCKET"/ > /tmp/antes.json        # chave duora-backup-verify
$C exec --no-TTY backup pgbackrest --stanza=duora --repo1-retention-full=1 expire   # $C de backup-restore.md
sudo duora-backup media-backup                                                # roda o restic forget --prune
b2 ls --versions --recursive --json b2://"$BUCKET"/ > /tmp/depois.json
python3 -c 'import json,sys
a,d=(json.load(open(f)) for f in sys.argv[1:3])
def current(xs):
    latest={}
    for x in xs:
        if x["fileName"] not in latest or x["uploadTimestamp"] > latest[x["fileName"]]["uploadTimestamp"]:
            latest[x["fileName"]]=x
    return {x["fileId"] for x in latest.values() if x["action"] == "upload"}
ids=lambda xs: {x["fileId"] for x in xs if x["action"] == "upload"}
gone=ids(a)-ids(d)
print("versões correntes apagadas:", len(gone & current(a)), "| antigas limpas pelo lifecycle:", len(gone - current(a)),
      "| hide markers novos (saldo):", sum(x["action"] == "hide" for x in d) - sum(x["action"] == "hide" for x in a))' \
  /tmp/antes.json /tmp/depois.json
```

Esperado: `versões correntes apagadas: 0` e hide markers novos > 0 (o `expire` ocultou o full antigo e o WAL dele), sem
erro de autorização nos logs do pgBackRest e do restic. O lifecycle pode apagar, entre as duas listagens, versões
**já ocultas** há mais de 30 dias: elas aparecem em "antigas limpas pelo lifecycle" e não contam como falha, porque o
lifecycle nunca apaga a versão corrente. Só uma versão que era a corrente em `antes.json` e sumiu indica exclusão
real. Rode `b2 account clear` no fim. **Hipóteses que esta prova confirma** (não verificadas sem conta):

- `PutObject` num bucket com File Lock aceito sem `Content-MD5` (a AWS exige `Content-MD5` ou checksum quando há
  retenção padrão; se o B2 também exigir, o pgBackRest ou o restic falham ao gravar e o backup alerta);
- `DeleteObjects` (multi-delete, que o pgBackRest usa no `expire`) sem `versionId` aceito com `writeFiles` e sem
  `deleteFiles`. A documentação da Backblaze lista o `DeleteObjects` sob as duas capacidades; se exigir
  `deleteFiles`, o `expire` falha (o repositório cresce, nada se perde) e a decisão volta ao responsável.

A cada trimestre, com a `duora-backup-verify` fora da VPS: `b2 ls --versions --recursive b2://"$BUCKET"/duora/`
mostra o repositório e os hide markers; um volume de hide markers fora do ritmo do `expire` é sinal de exclusão.

## Rotação das chaves (a cada 90 dias, ou já, se houver suspeita)

1. `b2 key create` da `duora-backup` com os mesmos parâmetros do passo 3 (a antiga continua valendo).
2. Na VPS, o comando do passo 4 com `--replace`; depois recrie o `backup` com os env files da release corrente
   ([deploy.md](deploy.md#operação-manual)) e confira:

   ```sh
   S=/var/lib/duora
   ID=$(sudo python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["releaseId"])' $S/current.json)
   sudo docker compose -f /opt/duora/compose.prod.yml --env-file /etc/duora/duora.env \
     --env-file $S/releases/$ID.env up -d --no-deps --force-recreate backup
   sudo duora-backup verify
   ```

3. Um dia depois, sem erro no verify, `b2 key delete <keyID antiga>`. A chave do B2 não tem estado "inativa": até o
   delete ela vale de qualquer lugar (o B2 não prende chave a IP).

## Recuperação depois de uma exclusão maliciosa

Passo 2 de "Recuperação depois de uma exclusão maliciosa" em [backup-restore.md](backup-restore.md), com a
`duora-backup-admin` criada na hora: liste os hide markers gravados depois do ataque, nos **dois** prefixos
(`duora/` do pgBackRest e `media/` do restic), e apague **só eles** (nunca versões de arquivo). `ATAQUE_MS` é o
instante UTC do ataque em milissegundos desde a época.

```sh
for prefix in duora/ media/; do
  b2 ls --versions --recursive --json b2://"$BUCKET"/"$prefix"
done | python3 -c 'import json,sys; t=int(sys.argv[1])
dec=json.JSONDecoder(); text=sys.stdin.read(); i=0; out=[]
while i < len(text):
    if text[i].isspace(): i += 1; continue
    chunk, i = dec.raw_decode(text, i); out += chunk
[print(f["fileId"]) for f in out if f["action"] == "hide" and f["uploadTimestamp"] >= t]' "$ATAQUE_MS" \
  > hide-markers.txt
wc -l hide-markers.txt     # confira o volume antes de apagar
while read -r id; do b2 rm "b2id://$id"; done < hide-markers.txt
b2 account clear
```

**Sobrescrita maliciosa** (a chave com `writeFiles` grava uma versão nova por cima, em vez de apagar): a versão
anterior continua no bucket, travada até 21 dias desde que foi gravada. Os arquivos que importam são os de controle,
reescritos a cada backup e por isso quase sempre dentro do lock:

| Repositório | Arquivos de controle |
|---|---|
| pgBackRest (`duora/`) | `duora/backup/duora/backup.info`, `backup.info.copy`, `duora/archive/duora/archive.info`, `archive.info.copy` e o `backup.manifest` de cada backup |
| restic (`media/`) | `media/config` e `media/keys/*` (sem eles o repositório não abre); `media/index/*` e `media/snapshots/*` se reconstroem com `restic repair index` e `restic repair snapshots` |

Para cada arquivo sobrescrito, ache a última versão anterior ao ataque e copie-a por id para o mesmo nome; a cópia vira
a versão corrente (a versão maliciosa fica, oculta, até o lifecycle):

```sh
b2 ls --versions --long b2://"$BUCKET"/duora/backup/duora/backup.info   # anote o fileId da versão anterior ao ataque
b2 file copy-by-id <fileId anterior> "$BUCKET" duora/backup/duora/backup.info
```

Depois, `sudo duora-backup verify` e `verify-repo` (banco) e `sudo duora-backup media-verify` e `media-check` (fotos).
Arquivos de dados (segmentos de WAL, pacotes do restic) não são reescritos pelas ferramentas; um sobrescrito aparece no
`verify-repo` ou no `media-check` e se recupera do mesmo jeito.

## Custo esperado

Preços de tabela pública da Backblaze, **a conferir** em <https://www.backblaze.com/cloud-storage/pricing> na criação:
armazenamento na casa de US$ 6 a 7 por TB-mês (os primeiros 10 GB gratuitos), download gratuito até 3× o volume médio
armazenado no mês e cerca de US$ 0,01/GB acima disso, upload gratuito. File Lock e versões não têm tarifa própria:
cobram o espaço guardado.

No exemplo de dimensionamento de [backup-restore.md](backup-restore.md#retenção-janela-recuperável-de-14-dias)
(~29 GB correntes + até ~29 GB de versões ocultas), o armazenamento fica abaixo de US$ 0,50 por mês. O download do
`verify-repo-full` semanal, do `verify-repo` diário e de um ensaio mensal de restore (~165 GB por mês) fica perto da
franquia de 3× (~170 GB): no pior caso, alguns centavos. Fotos somam pouco espaço (10 mil fotos ≈ 2 GB, mais as versões
do restic) e o único download delas é o `media-check` semanal; a leitura no uso normal sai do disco da VPS.
