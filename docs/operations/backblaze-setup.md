# Bucket e chaves no Backblaze B2 (US East)

Runbook para o **responsável** criar, uma vez, o bucket de backup do Duora no Backblaze B2 e as application keys
restritas. Nenhum agente roda estes comandos: eles exigem o login da conta B2, que nunca entra na VPS nem no CI.
Decisão do provedor: Backblaze B2, região US East (responsável, 2026-10-16; adendo do
[ADR-0023](../architecture/adr/0023-topologia-do-backup-repository-host-e-object-lock.md)). As fotos ficam no disco
da VPS (adendo do [ADR-0010](../architecture/adr/0010-object-storage-externo-leitura-via-api.md)) e não têm bucket de
leitura; a cópia delas para este bucket é a proposta da seção [Fotos](#fotos-proposta-a-aprovar).

| Item | Valor |
|---|---|
| Bucket | Um só, privado (`allPrivate`), **próprio do Duora**: a conta B2 é compartilhada com outro projeto |
| Proteção | File Lock (Object Lock) ligado na criação, retenção padrão `compliance` de 21 dias, lifecycle de versões ocultas |
| Prefixos | `duora/` (repositório do pgBackRest, `repo1-path=/duora`); `media/` reservado para o backup das fotos |
| Cifra | SSE-B2 por padrão, além da cifra do pgBackRest no cliente (`aes-256-cbc`): a Backblaze só vê blocos cifrados |
| Endpoint | `s3.us-east-005.backblazeb2.com` é o provável; **confirmar no bucket** (passo 1) |

O B2 não tem política de bucket nem IAM: tudo o que restringe o acesso mora nas **application keys** (bucket, prefixo
de nome e capacidades). Por isso o `duora-backup verify` confere a chave da VPS a cada execução (passo 6).

## 0. Pré-requisitos (máquina do responsável)

- CLI oficial do B2, versão 4 (`pipx install b2` ou `brew install b2-tools`). Confira com `b2 version`; os comandos
  abaixo seguem a documentação do CLI 4 e podem mudar de nome em outra versão (`b2 --help`).
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
| `duora-backup-admin` | criada só quando for usada | nenhum | `listFiles,readFiles,writeFiles,deleteFiles,readBuckets,writeBuckets,readBucketRetentions,readFileRetentions` | Remover hide markers depois de um ataque, mudar lifecycle |

```sh
b2 key create --bucket "$BUCKET" --name-prefix duora/ duora-backup \
  listFiles,readFiles,writeFiles,readBuckets,readBucketRetentions
b2 key create --bucket "$BUCKET" --name-prefix duora/ duora-backup-verify \
  listFiles,readFiles,readBuckets,readBucketRetentions
```

Por que essas capacidades (tabela de operações S3 × capacidades da Backblaze):

- `writeFiles` cobre `PutObject`, o multipart e o `DeleteObject` **sem** `versionId`, que no bucket versionado só
  grava um hide marker. É o que o `expire` do pgBackRest faz.
- **Sem `deleteFiles`**: é ela que apaga uma versão (`DeleteObject` com `versionId`). Sem ela, nem um atacante com a
  chave da VPS apaga o histórico; com o lock, nem quem tiver `deleteFiles` apaga os últimos 21 dias.
- `readBuckets` (`GetBucketVersioning`) e `readBucketRetentions` (`GetObjectLockConfiguration`): o verify lê os dois.
- Nenhuma capacidade de retenção, legal hold, bucket, chaves, compartilhamento (`shareFiles`) ou `bypassGovernance`.
  O verify reprova qualquer capacidade fora da lista, inclusive uma que a Backblaze criar no futuro.

**Chave administrativa sob demanda** (recomendação; decisão do responsável): o B2 não exige 2FA para usar uma
application key, então uma chave com `deleteFiles` guardada no cofre é uma credencial de apagar sem segundo fator.
Crie-a só na hora, com validade curta, a partir do login com 2FA, e apague-a depois:

```sh
b2 key create --bucket "$BUCKET" --duration 3600 duora-backup-admin \
  listFiles,readFiles,writeFiles,deleteFiles,readBuckets,writeBuckets,readBucketRetentions,readFileRetentions
# ... uso ...
b2 key delete <keyID da duora-backup-admin>
```

Ao terminar o passo 3, apague a chave temporária de administração do passo 0 (`b2 key delete`, ou na web) e rode
`unset B2_APPLICATION_KEY_ID B2_APPLICATION_KEY`.

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
| `duora-backup-admin` | em lugar nenhum: nasce e morre no uso |
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

**Na máquina do responsável** (provas negativas), com a chave do backup num subshell e um canário:

```sh
(
  read -r B2_APPLICATION_KEY_ID; read -rs B2_APPLICATION_KEY; export B2_APPLICATION_KEY_ID B2_APPLICATION_KEY
  echo canario > /tmp/canario.txt
  b2 file upload "$BUCKET" /tmp/canario.txt duora/canary/lock-test     # esperado: sucesso
  b2 rm b2://"$BUCKET"/duora/canary/lock-test                         # esperado: recusa (sem deleteFiles)
  b2 ls b2://"$BUCKET"/media/                                         # esperado: recusa (fora do prefixo)
  rm /tmp/canario.txt
)
```

Depois, com a `duora-backup-admin` criada só para isso (passo 3): `b2 rm b2://"$BUCKET"/duora/canary/lock-test`
→ recusa pelo File Lock `compliance`, mesmo com `deleteFiles`. Apague a chave admin em seguida. O canário fica até o
lock vencer e o lifecycle limpá-lo.

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
`duora-backup-admin` criada na hora: liste os hide markers gravados depois do ataque e apague **só eles** (nunca
versões de arquivo). `ATAQUE_MS` é o instante UTC do ataque em milissegundos desde a época.

```sh
b2 ls --versions --recursive --json b2://"$BUCKET"/duora/ \
  | python3 -c 'import json,sys; t=int(sys.argv[1]); [print(f["fileId"]) for f in json.load(sys.stdin)
                if f["action"] == "hide" and f["uploadTimestamp"] >= t]' "$ATAQUE_MS" > hide-markers.txt
wc -l hide-markers.txt     # confira o volume antes de apagar
while read -r id; do b2 rm "b2id://$id"; done < hide-markers.txt
```

## Fotos (proposta a aprovar)

As fotos aprovadas ficam no disco da VPS (adendo do ADR-0010) e precisam de cópia fora dela antes do upload ser
ligado em produção. Proposta (ferramenta nova, **decisão do responsável**): `restic` para o prefixo `media/` deste
bucket, com repositório cifrado no cliente, chave própria e as mesmas proteções:

```sh
b2 key create --bucket "$BUCKET" --name-prefix media/ duora-media-backup \
  listFiles,readFiles,writeFiles,readBuckets,readBucketRetentions
```

Nada disso existe ainda: a chave só é criada no PR que implementar o backup das fotos.

## Custo esperado

Preços de tabela pública da Backblaze, **a conferir** em <https://www.backblaze.com/cloud-storage/pricing> na criação:
armazenamento na casa de US$ 6 a 7 por TB-mês (os primeiros 10 GB gratuitos), download gratuito até 3× o volume médio
armazenado no mês e cerca de US$ 0,01/GB acima disso, upload gratuito. File Lock e versões não têm tarifa própria:
cobram o espaço guardado.

No exemplo de dimensionamento de [backup-restore.md](backup-restore.md#retenção-janela-recuperável-de-14-dias)
(~29 GB correntes + até ~29 GB de versões ocultas), o armazenamento fica abaixo de US$ 0,50 por mês. O download do
`verify-repo-full` semanal, do `verify-repo` diário e de um ensaio mensal de restore (~165 GB por mês) fica perto da
franquia de 3× (~170 GB): no pior caso, alguns centavos. Fotos somam pouco espaço (10 mil fotos ≈ 2 GB) e não geram
download no uso normal, porque a leitura sai do disco da VPS.
