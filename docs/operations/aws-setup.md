# Buckets e credenciais na AWS (us-east-1)

Runbook para o **responsável** criar, uma vez, os dois buckets S3 do Duora e as credenciais restritas que vão para a
VPS. Nenhum agente roda estes comandos: eles exigem a conta AWS e uma identidade administrativa que nunca entra na
VPS nem no CI. Decisão do provedor: AWS S3 em `us-east-1` (responsável, 2026-10-16; adendos do
[ADR-0023](../architecture/adr/0023-topologia-do-backup-repository-host-e-object-lock.md) e do
[ADR-0025](../architecture/adr/0025-pipeline-de-fotos-imagesharp-s3-outbox.md)).

| Bucket | Uso | Proteção | Credenciais na VPS |
|---|---|---|---|
| backup | Repositório do pgBackRest (prefixo `duora/`) | Versionamento, Object Lock `COMPLIANCE` de 21 dias, lifecycle de versões não correntes, política do bucket que nega apagar versão a todos menos ao papel administrativo | `duora-backup` (container `backup` e job `restore`) |
| mídia | Fotos: `quarantine/` (originais) e `derivatives/` (variantes) | **Sem** versionamento e sem Object Lock: apagar uma foto apaga de fato (spec 12.4 e 16.2) | `duora-media-web` e `duora-media-worker`, uma por processo |

Os dois buckets: acesso público bloqueado, ACLs desligadas (`BucketOwnerEnforced`), SSE-S3 (`AES256`) por padrão e
política que recusa HTTP sem TLS e TLS abaixo de 1.2. O pgBackRest ainda cifra do lado do cliente
(`aes-256-cbc`): a AWS só vê blocos cifrados do backup.

Todo o JSON aplicado aqui está versionado em [`infra/aws/`](../../infra/aws) e é conferido por
`scripts/deploy/test_duora_aws.py` (nenhum curinga em `Allow`, recursos restritos ao bucket e ao prefixo, negações
presentes, lock ≥ o mínimo do `duora-backup verify`). Mudar uma permissão é mudar o JSON **e** o teste no mesmo PR.

## 0. Pré-requisitos (máquina do responsável)

- AWS CLI v2 e Python 3.12 com este repositório.
- Uma identidade administrativa **IAM user com MFA** (perfil `duora-admin` abaixo), não o root. O root fica com MFA
  de hardware e sem access key. Um usuário do IAM Identity Center não serve como origem do papel
  `duora-backup-admin`: a sessão dele não traz `aws:MultiFactorAuthPresent`, e a política de confiança exige MFA.
- O IPv4 público de saída da VPS (`curl -4 https://checkip.amazonaws.com` **na VPS**). As credenciais da VPS só
  funcionam a partir dele (`aws:SourceIp`); trocar de VPS (por exemplo, no restore depois de comprometimento) exige
  renderizar e reaplicar as três políticas de usuário com o IP novo.

```sh
export AWS_PROFILE=duora-admin AWS_REGION=us-east-1
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
# Os mesmos nomes do inventário (duora_s3_backup_bucket e duora_s3_media_bucket). Sem ponto e sem dado pessoal;
# sugestão: duora-<ambiente>-<backup|media>-<sufixo aleatório>, por exemplo com "openssl rand -hex 3".
BACKUP_BUCKET=duora-prod-backup-<sufixo>
MEDIA_BUCKET=duora-prod-media-<sufixo>
VPS_IPV4=<IPv4 público da VPS>
OUT=~/duora-ops/aws-$(date -u +%Y%m%d)
```

## 1. Preencher os modelos

```sh
python3 scripts/deploy/duora_aws.py render --backup-bucket "$BACKUP_BUCKET" --media-bucket "$MEDIA_BUCKET" \
  --account-id "$ACCOUNT_ID" --vps-ipv4 "$VPS_IPV4" --out "$OUT"
```

O comando recusa nome de bucket inválido ou com ponto, conta que não tenha 12 dígitos, IP que não seja IPv4 público,
os dois buckets com o mesmo nome e um `--out` que já tenha arquivos. Ele não fala com a AWS.

## 2. Bucket de backup

O Object Lock só pode ser ligado na criação. Em `us-east-1` o `create-bucket` não leva `LocationConstraint`.

```sh
aws s3api create-bucket --bucket "$BACKUP_BUCKET" --object-lock-enabled-for-bucket
aws s3api put-public-access-block --bucket "$BACKUP_BUCKET" \
  --public-access-block-configuration "file://$OUT/buckets/backup/public-access-block.json"
aws s3api put-bucket-ownership-controls --bucket "$BACKUP_BUCKET" \
  --ownership-controls "file://$OUT/buckets/backup/ownership-controls.json"
aws s3api put-bucket-encryption --bucket "$BACKUP_BUCKET" \
  --server-side-encryption-configuration "file://$OUT/buckets/backup/encryption.json"
aws s3api put-bucket-versioning --bucket "$BACKUP_BUCKET" \
  --versioning-configuration "file://$OUT/buckets/backup/versioning.json"
aws s3api put-object-lock-configuration --bucket "$BACKUP_BUCKET" \
  --object-lock-configuration "file://$OUT/buckets/backup/object-lock.json"
aws s3api put-bucket-lifecycle-configuration --bucket "$BACKUP_BUCKET" \
  --lifecycle-configuration "file://$OUT/buckets/backup/lifecycle.json"
```

- `object-lock.json`: retenção padrão `COMPLIANCE` de 21 dias, a janela máxima do repositório. Em `COMPLIANCE`
  ninguém, nem o root, apaga ou encurta a retenção de uma versão antes do prazo. **Não teste com um prazo maior por
  engano**: um bucket com lock longo não pode ser esvaziado até vencer.
- `lifecycle.json`: versões não correntes expiram 30 dias depois de deixarem de ser correntes (21 do lock + margem),
  delete markers órfãos somem e uploads multipart abandonados são abortados em 7 dias.

## 3. Papel administrativo do backup

Única identidade que pode apagar versões (os delete markers de uma exclusão maliciosa), trocar o lifecycle ou a
política do bucket de backup. Assumido só com MFA recente, fora da VPS.

```sh
aws iam create-role --role-name duora-backup-admin --max-session-duration 3600 \
  --assume-role-policy-document "file://$OUT/iam/duora-backup-admin-trust.json"
aws iam put-role-policy --role-name duora-backup-admin --policy-name duora-backup-admin \
  --policy-document "file://$OUT/iam/duora-backup-admin.json"
```

Perfil para assumir o papel (em `~/.aws/config`):

```ini
[profile duora-backup-admin]
role_arn = arn:aws:iam::<ACCOUNT_ID>:role/duora-backup-admin
source_profile = duora-admin
mfa_serial = arn:aws:iam::<ACCOUNT_ID>:mfa/<dispositivo>
duration_seconds = 3600
```

## 4. Política do bucket de backup (por último)

Depois dela, nem o `duora-admin` muda lock, versionamento, lifecycle, política, ACL, cifra ou replicação, nem apaga
versão: só o papel `duora-backup-admin`. Por isso ela vem depois de toda a configuração.

```sh
aws s3api put-bucket-policy --bucket "$BACKUP_BUCKET" --policy "file://$OUT/buckets/backup/policy.json"
```

Limite que a AWS impõe: o **root** da conta sempre consegue apagar ou trocar a política de um bucket (é o mecanismo
contra autobloqueio). A negação vale para o root em todas as outras ações, e o lock `COMPLIANCE` continua valendo
para ele: no pior caso, o root comprometido apaga versões com mais de 21 dias, nunca as do lock. Daí o root com MFA de
hardware e sem access key.

## 5. Bucket de mídia

Sem `--object-lock-enabled-for-bucket` e **sem** `put-bucket-versioning`: o bucket nasce sem versionamento e assim
fica.

```sh
aws s3api create-bucket --bucket "$MEDIA_BUCKET"
aws s3api put-public-access-block --bucket "$MEDIA_BUCKET" \
  --public-access-block-configuration "file://$OUT/buckets/media/public-access-block.json"
aws s3api put-bucket-ownership-controls --bucket "$MEDIA_BUCKET" \
  --ownership-controls "file://$OUT/buckets/media/ownership-controls.json"
aws s3api put-bucket-encryption --bucket "$MEDIA_BUCKET" \
  --server-side-encryption-configuration "file://$OUT/buckets/media/encryption.json"
aws s3api put-bucket-lifecycle-configuration --bucket "$MEDIA_BUCKET" \
  --lifecycle-configuration "file://$OUT/buckets/media/lifecycle.json"
aws s3api put-bucket-policy --bucket "$MEDIA_BUCKET" --policy "file://$OUT/buckets/media/policy.json"
```

`lifecycle.json` da mídia: rede de segurança que apaga originais esquecidos em `quarantine/` depois de 3 dias (a
entrega `media-processing` desiste em 24 h; o original com EXIF não deve sobreviver a uma queda no meio do
processamento) e aborta uploads multipart incompletos em 1 dia.

## 6. Usuários e políticas da VPS

A VPS não está na AWS, então as credenciais são access keys de IAM users (o `duora-backup verify` só assina com
chave compartilhada, `repo1-s3-key-type=shared`). Uma por processo, com política inline:

```sh
for user in duora-backup duora-media-web duora-media-worker; do
  aws iam create-user --user-name "$user"
  aws iam put-user-policy --user-name "$user" --policy-name "$user" --policy-document "file://$OUT/iam/$user.json"
done
```

| Usuário | Arquivo | Pode | Não pode |
|---|---|---|---|
| `duora-backup` | `iam/duora-backup.json` | Listar (com e sem versões) só o prefixo `duora/`; ler versionamento e Object Lock (verify); ler, gravar e apagar sem `versionId` em `duora/*`; ler versões antigas (`--repo-target-time`) | Apagar versão, contornar ou mudar retenção, mudar versionamento, lock, lifecycle, política, ACL, bloqueio público, cifra, posse ou replicação; qualquer coisa sem TLS ou fora do IP da VPS |
| `duora-media-web` | `iam/duora-media-web.json` | Gravar e apagar em `quarantine/*` (o apagar é a compensação de upload que falhou); ler `derivatives/*` | Ler a quarentena, listar o bucket, gravar derivados |
| `duora-media-worker` | `iam/duora-media-worker.json` | Ler e apagar em `quarantine/*`; gravar, ler e apagar em `derivatives/*` | Listar o bucket, gravar na quarentena |

Chave de acesso: crie **agora só a do `duora-backup`**. As da mídia esperam o upload de fotos ser ligado em produção
(hoje ele está desligado, `Media:Review` ausente, ADR-0025 item 8, e o `compose.prod.yml` ainda não monta essas
chaves): chave que não é usada não deve existir.

```sh
aws iam create-access-key --user-name duora-backup \
  --query 'AccessKey.[AccessKeyId,SecretAccessKey]' --output text
```

Copie as duas partes direto para o cofre do responsável; não grave em arquivo nem cole em chat, issue ou commit.

## 7. Gravar a credencial na VPS

Na VPS, como o usuário administrativo. O `read -s` não ecoa o segredo nem o deixa no histórico do shell; o
`duora-secrets` recusa valor com espaço ou quebra de linha (o arquivo é INI) e nunca repete o segredo num erro.

```sh
( IFS= read -r -p 'access key id: ' key; IFS= read -r -s -p 'secret access key: ' secret; echo >&2
  printf '%s\n%s\n' "$key" "$secret" ) \
  | sudo duora-secrets write-backup-repo /etc/duora/secrets --bucket <BACKUP_BUCKET>
```

Grava `/etc/duora/secrets/pgbackrest_repo.conf` (0400, dono 999) com `repo1-s3-endpoint=s3.us-east-1.amazonaws.com`
(só o host, sem `https://`), `repo1-s3-region=us-east-1` e `repo1-s3-uri-style=host`. Os padrões são os de
`duora_s3_region`, `duora_s3_endpoint` e `duora_s3_uri_style` em `infra/ansible/group_vars/all.yml`. Se o arquivo já
existir, o comando recusa; na rotação, use `--replace` (troca por rename atômico). A senha de cifra
(`pgbackrest_cipher.conf`) continua como em [backup-restore.md](backup-restore.md#segredos-do-repositório-operador-uma-vez).

Filtro de saída do container `backup`: no inventário real, `duora_backup_s3_cidrs` recebe os prefixos IPv4 do S3 em
`us-east-1`, publicados pela AWS:

```sh
curl -fsS https://ip-ranges.amazonaws.com/ip-ranges.json | python3 scripts/deploy/duora_aws.py s3-cidrs
```

A saída já vem no formato YAML do inventário. Rode o playbook (role `backup`) para reaplicar o filtro. Os prefixos
mudam: renove a lista a cada trimestre ou quando o verify passar a falhar por conexão recusada. Limite: o filtro
libera **todo** o S3 da região, inclusive buckets de terceiros; um `backup` comprometido ainda poderia exfiltrar para
um bucket do atacante em `us-east-1`. A defesa contra isso é o pgBackRest cifrar no cliente e o container não ter
outro dado além do PGDATA que já é o backup.

Depois, a sequência de [backup-restore.md](backup-restore.md#primeira-configuração) (stanza, full e verify).

## 8. Validar

**No servidor** (prova positiva; o deploy só segue com ela):

```sh
sudo duora-backup verify
```

Ele assina `GET ?versioning` e `GET ?object-lock` com a credencial do `duora-backup`, de dentro do container, e falha
fechado sem versionamento `Enabled` e lock `COMPLIANCE` de pelo menos 21 dias. Uma resposta 403 aqui quer dizer
política errada (ou IP da VPS diferente do renderizado).

**Na máquina do responsável** (provas negativas, gate do ADR-0025 "teste de permissão no fornecedor escolhido"):

1. A chave do `duora-backup` fora da VPS é recusada (prova o `aws:SourceIp`): com um perfil temporário dela no
   laptop, `aws s3api get-bucket-versioning --bucket "$BACKUP_BUCKET"` → `AccessDenied`. Apague o perfil depois.
2. Simulação das políticas de usuário com o IP e o TLS da VPS (o simulador avalia as políticas de identidade sem
   tocar nos objetos):

   ```sh
   context="ContextKeyName=aws:SourceIp,ContextKeyValues=$VPS_IPV4,ContextKeyType=ip \
   ContextKeyName=aws:SecureTransport,ContextKeyValues=true,ContextKeyType=boolean"
   # Esperado: allowed
   aws iam simulate-principal-policy --policy-source-arn "arn:aws:iam::$ACCOUNT_ID:user/duora-backup" \
     --action-names s3:PutObject s3:GetObjectVersion s3:DeleteObject \
     --resource-arns "arn:aws:s3:::$BACKUP_BUCKET/duora/archive/x" --context-entries $context \
     --query 'EvaluationResults[].[EvalActionName,EvalDecision]' --output text
   # Esperado: explicitDeny
   aws iam simulate-principal-policy --policy-source-arn "arn:aws:iam::$ACCOUNT_ID:user/duora-backup" \
     --action-names s3:DeleteObjectVersion s3:BypassGovernanceRetention s3:PutObjectRetention \
     --resource-arns "arn:aws:s3:::$BACKUP_BUCKET/duora/archive/x" --context-entries $context \
     --query 'EvaluationResults[].[EvalActionName,EvalDecision]' --output text
   # Esperado: implicitDeny (o web não lê a quarentena) e allowed (lê derivados)
   aws iam simulate-principal-policy --policy-source-arn "arn:aws:iam::$ACCOUNT_ID:user/duora-media-web" \
     --action-names s3:GetObject \
     --resource-arns "arn:aws:s3:::$MEDIA_BUCKET/quarantine/x" "arn:aws:s3:::$MEDIA_BUCKET/derivatives/x" \
     --context-entries $context --query 'EvaluationResults[].[EvalResourceName,EvalDecision]' --output text
   ```

3. O lock vale até para o papel administrativo: grave um objeto-canário e tente apagar a versão.

   ```sh
   version=$(echo canario | aws s3api put-object --bucket "$BACKUP_BUCKET" --key canary/lock-test --body /dev/stdin \
     --checksum-algorithm SHA256 --query VersionId --output text)
   # Esperado: AccessDenied pela política do bucket (o duora-admin não é o papel).
   aws s3api delete-object --bucket "$BACKUP_BUCKET" --key canary/lock-test --version-id "$version"
   # Esperado: AccessDenied por Object Lock (COMPLIANCE), mesmo com o papel.
   aws --profile duora-backup-admin s3api delete-object --bucket "$BACKUP_BUCKET" --key canary/lock-test \
     --version-id "$version"
   # Limpeza: delete marker agora; o lifecycle remove a versão 30 dias depois de o lock vencer.
   aws s3api delete-object --bucket "$BACKUP_BUCKET" --key canary/lock-test
   ```

4. HTTP sem TLS é recusado: `curl -sI "http://$BACKUP_BUCKET.s3.us-east-1.amazonaws.com/"` → `403`.

## Configuração da mídia (quando o upload for ligado)

O adaptador `S3ObjectStore` (`Media:Storage`) na AWS: `Provider=S3`, `S3:Region=us-east-1`, `S3:Bucket=<MEDIA_BUCKET>`
e **`S3:ServiceUrl` vazio**, para o SDK resolver o endpoint regional com host virtual
(`https://<bucket>.s3.us-east-1.amazonaws.com`). Com `ServiceUrl=https://s3.us-east-1.amazonaws.com` o adaptador força
path style, que a AWS mantém só por compatibilidade (`duora_media_s3_service_url` vazio no `group_vars`). As chaves
entram por arquivo (Compose secrets), uma por processo, como chaves de configuração `Media__Storage__S3__AccessKeyId`
e `Media__Storage__S3__SecretAccessKey`; montar esses arquivos no `web` e no `worker` é parte do PR que ligar o upload
em produção (ADR-0025, gate "antes de dados reais").

## Rotação das chaves (a cada 90 dias, ou já, se houver suspeita)

1. `aws iam create-access-key --user-name duora-backup` (o IAM permite duas chaves ativas por usuário).
2. Na VPS, o comando do passo 7 com `--replace`; depois recrie o `backup` com os env files da release corrente
   ([deploy.md](deploy.md#operação-manual)) e confira:

   ```sh
   S=/var/lib/duora
   ID=$(sudo python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["releaseId"])' $S/current.json)
   sudo docker compose -f /opt/duora/compose.prod.yml --env-file /etc/duora/duora.env \
     --env-file $S/releases/$ID.env up -d --no-deps --force-recreate backup
   sudo duora-backup verify
   ```
3. `aws iam update-access-key --user-name duora-backup --access-key-id <antiga> --status Inactive`; um dia depois,
   sem erro no verify, `aws iam delete-access-key ... --access-key-id <antiga>`.

## Recuperação depois de uma exclusão maliciosa

Passo 2 de "Recuperação depois de uma exclusão maliciosa" em [backup-restore.md](backup-restore.md), com o papel:
liste os delete markers posteriores ao ataque e apague **só eles** (nunca versões).

```sh
aws --profile duora-backup-admin s3api list-object-versions --bucket "$BACKUP_BUCKET" --prefix duora/ \
  --query "DeleteMarkers[?LastModified>='<instante UTC do ataque>'].[Key,VersionId]" --output text \
  | while read -r key version; do
      aws --profile duora-backup-admin s3api delete-object --bucket "$BACKUP_BUCKET" --key "$key" --version-id "$version"
    done
```

## Custo esperado

Preços de tabela pública da AWS para `us-east-1` na data deste documento, **a conferir** em
<https://aws.amazon.com/s3/pricing/> ao criar a conta: S3 Standard US$ 0,023/GB-mês; PUT/LIST US$ 0,005 por mil;
GET US$ 0,0004 por mil; entrada gratuita; saída para a internet com 100 GB/mês gratuitos somados entre todos os
serviços da conta e, depois, cerca de US$ 0,09/GB. Object Lock e versionamento não têm tarifa própria: cobram o
espaço das versões guardadas. A VPS está fora da AWS, então **toda leitura do S3 pela VPS é egress**.

Hipóteses do exemplo de dimensionamento de [backup-restore.md](backup-restore.md#retenção-janela-recuperável-de-14-dias)
(full de 2 GB, 200 MB/dia de páginas alteradas, 500 MB/dia de WAL), por mês:

| Item | Volume | Custo |
|---|---|---|
| Armazenamento do backup | ~29 GB correntes + até ~29 GB de versões não correntes (30 dias de lifecycle) | ~US$ 1,35 |
| Requisições do backup | WAL a cada 60 s (~45 mil PUT), backups com `repo1-bundle`, listagens do verify | < US$ 0,50 |
| Egress do `verify-repo-full` semanal | ~29 GB × 4,3 semanas ≈ 126 GB | |
| Egress do `verify-repo` diário | o backup mais novo (full de 2 GB no domingo, diferenciais de 0,2 a 1,2 GB) ≈ 27 GB | |
| Egress de ensaio de restore mensal | ~10 GB (full + diferencial + WAL) | |
| **Egress do backup** | **~165 GB**, dos quais ~65 GB acima da franquia | **~US$ 6** (US$ 15 sem a franquia) |
| Armazenamento da mídia | derivados WebP (~200 KB por foto nas três variantes); 10 mil fotos ≈ 2 GB | ~US$ 0,05 |
| Egress da mídia | **visualizações por mês × tamanho médio do derivado**: cada foto exibida passa do S3 à VPS (leitura só via API, ADR-0010). Hipótese do piloto: 300 pessoas × 100 fotos vistas por dia × 60 KB ≈ 54 GB | ~US$ 5 (divide a franquia com o backup) |

Ordem de grandeza: **US$ 10 a 20 por mês** no piloto, dominada pelo egress. O verify semanal completo é o maior item
fixo; o egress da mídia cresce com o uso e é o primeiro a medir (contagem de leituras de derivados por dia). Se
passar do orçamento, as alavancas são cache dos derivados na VPS (ADR-0010) e, para o backup, espaçar o
`verify-repo-full` (decisão do responsável de 2026-10-15). Crie um alerta de orçamento na conta (AWS Budgets, por
exemplo em US$ 25/mês); ele avisa, não bloqueia o consumo (spec 19.4).
