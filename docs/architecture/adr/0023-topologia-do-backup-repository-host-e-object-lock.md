# ADR-0023: Backup por repository host em container separado, mTLS e bucket com Object Lock

- **Status:** Accepted (2026-10-15, decisão do usuário)
- **Substituído em parte:** item "Jurisdição" do adendo do B2, pelo [ADR-0026](0026-publico-idiomas-e-regiao-do-servidor.md) (2026-10-19)
- **Origem:** spec 15.2, 18.2 e 18.3; revisão de segurança do PR #19 (`network-egress-control-missing` e exclusão
  pelo certificado do postgres)
- **Revisar quando:** o pgBackRest passar a restringir operações por certificado, o provedor S3 escolhido não
  suportar Object Lock, ou o banco sair da VPS da aplicação

## Contexto

O ADR-0011 fixou pgBackRest com WAL externo. Faltava decidir onde ficam a saída para o S3, as credenciais do
repositório e a proteção das cópias contra exclusão (spec 18.3, "resiliência a comprometimento"). A spec 18.2 descreve
o `archive_command` "dentro do ambiente com acesso ao `PGDATA` e às credenciais necessárias", o que poria a credencial
do S3 e a saída para a internet no container do banco.

Duas revisões mudaram o desenho:

- um banco com rota para fora facilita exfiltração se o processo for comprometido (spec 15.2);
- com o desenho por repository host, o certificado de cliente do postgres ainda lista e apaga o repositório:
  `tls-server-auth` do pgBackRest restringe a stanza, não as operações (`repo-ls`, `repo-rm --recurse` e `expire`
  funcionam a partir do postgres; o drill prova). Tirar a credencial do banco não basta contra exclusão.

Alternativas consideradas:

- **Archive no postgres com filtro `DOCKER-USER` por IP do endpoint.** Topologia mais simples, mas o banco continua
  com saída e com a credencial que apaga o repositório; endpoints S3 resolvem para IPs que mudam, e a allowlist por IP
  fica frágil.
- **Repository host sem proteção no bucket.** Protege saída e credencial, mas um postgres comprometido apaga backups
  e WAL pelo servidor TLS, e a credencial do `backup` apaga tudo.
- **Segunda cópia em outra conta com replicação.** Protege, mas exige segunda conta, replicação e monitoramento dela;
  mais caro de operar num piloto de um operador só. Fica como reforço futuro.
- **Agente pgBackRest (`pg1-host`) num terceiro container junto ao postgres.** O agente lê o PGDATA de qualquer
  forma; só moveria o acesso aos dados de container e traria mais um par de certificados.

## Decisão

Vamos rodar o pgBackRest do repositório num container `backup` separado (repository host, `pgbackrest server`), o
único com saída para o S3 (rede `backup-egress`, filtrada no host) e com a credencial do S3 e a senha de cifra. O
postgres fica só em redes `internal` e envia o WAL por TLS mútuo (CA própria, certificado de cliente
`duora-postgres`, `tls-server-auth` só para a stanza `duora`) pela rede interna `pgbackrest`, com IP fixo do
`backup`. O `backup` lê o PGDATA só leitura e fala SQL com o papel mínimo `duora_backup` por TCP.

Vamos proteger as cópias contra exclusão no próprio bucket: versionamento habilitado, Object Lock com retenção
padrão `COMPLIANCE` de pelo menos 21 dias (a janela máxima de retenção), lifecycle que expira versões não correntes
depois dessa janela e credencial do `backup` sem `s3:DeleteObjectVersion`, sem bypass de retenção e sem poder de
alterar versionamento, lock, lifecycle ou a política do bucket. Isso é **pré-requisito de produção**: o
`duora-backup verify` falha fechado sem versionamento e Object Lock, e o deploy para.

Porquê técnico: a saída e a credencial saem do processo exposto ao SQL da aplicação, e a exclusão por qualquer
credencial que exista na VPS vira delete marker recuperável por `--repo-target-time`. Porquê de negócio: recuperação
depois de comprometimento é condição de lançamento (spec 18.5), com custo de um bucket só.

## Consequências

- Positivas: postgres sem rota para fora; exclusão maliciosa recuperável dentro da janela; `expire` do pgBackRest
  continua funcionando (apagar sem `versionId` só cria delete marker).
- Negativas: um container a mais; CA e certificados para rotacionar (825 dias); o provedor precisa suportar Object
  Lock na criação do bucket; versões não correntes ocupam espaço até o lifecycle (até perto do dobro); o verify faz
  duas consultas assinadas ao bucket a cada execução.
- Custo de leitura (decisão do usuário, 2026-10-15: verificação diária leve e completa semanal): por dia, a listagem
  do archive e o `pgbackrest verify --set` do backup mais novo (um full ou diferencial baixado e decifrado); por
  semana, o repositório inteiro (no pior momento 3 fulls, 18 diferenciais e 21 dias de WAL, ~29 GB no exemplo de
  dimensionamento do manual). Com tarifa de egress, isso entra na escolha do provedor.
- O Object Lock conta desde a criação de cada versão, não desde a exclusão: objeto gravado há mais de 21 dias já
  está fora do lock. Por isso a **defesa principal é a negação no IAM** (a credencial do `backup`, a única na VPS, não
  apaga versão nem altera retenção, versionamento, lock ou lifecycle). O lock é defesa em profundidade para os
  últimos 21 dias, caso a negação falhe.
- Trade-off: a topologia não impede o postgres de apagar o repositório; ela só garante que a exclusão é
  recuperável. Restaurar depois de um ataque exige a credencial administrativa fora da VPS para remover os delete
  markers.

## Segurança e operação

Superfície: o servidor TLS só escuta no IP da rede `pgbackrest`; nenhum container recebe o socket do Docker. Segredos:
credencial S3 e senha de cifra só no `backup` e no job `restore`; a credencial administrativa do bucket nunca entra na
VPS. Operação: procedimento de recuperação após exclusão em `docs/operations/backup-restore.md`; o drill de restore
roda o ataque e a recuperação no CI.

## Compliance

- `duora-backup verify` recusa bucket sem versionamento, sem Object Lock, com modo diferente de `COMPLIANCE` ou com
  retenção menor que 21 dias (`BucketProtectionTests`, `BucketProtectionActionTests`).
- Drill: uma versão travada recusa `mc rm --version-id` mesmo do usuário root do dublê.
- Drill de restore no job `images` do CI: isolamento de rede, `repo-ls` e `repo-rm --recurse` do postgres funcionando
  (documenta o poder), restore por `--repo-target-time` depois da exclusão e `verify-repo` acusando lacuna de WAL.
- Filtro de saída conferido pelo verify no host (`EgressFilterCheckTests`); prova no host real pendente (M3).

## Adendo (2026-10-16): provedor Backblaze B2, região US East

Decisão do responsável: o repositório fica num bucket **Backblaze B2 na região US East**, próprio do Duora (a conta
B2 é compartilhada com outro projeto), criado com File Lock (o Object Lock do B2). A AWS foi descartada. A decisão
acima não muda; o adendo fixa como ela fica no B2. Criação, chaves e validação:
[backblaze-setup.md](../../operations/backblaze-setup.md).

- **Sem política de bucket nem IAM.** O B2 restringe acesso só pela application key (bucket, prefixo de nome e
  capacidades). A "negação no IAM" desta decisão vira: a chave `duora-backup`, a única na VPS, presa ao bucket e ao
  prefixo `duora/`, com `listFiles`, `readFiles`, `writeFiles`, `readBuckets` e `readBucketRetentions`, **sem**
  `deleteFiles` (apagar versão), sem capacidade de retenção, legal hold, bucket, chaves ou compartilhamento. Uma chave
  só de leitura (`duora-backup-verify`) fica no cofre para conferência fora da VPS; a administrativa (com
  `deleteFiles` e `writeBuckets`) nasce sob demanda com validade curta, porque application key no B2 não tem 2FA.
- **Prova a cada verify.** Como a restrição mora toda na chave e nada a versiona como código, o `duora-backup verify`
  chama o `b2_authorize_account` (API nativa v4) com a chave da VPS e falha fechado se ela tiver capacidade fora da
  allowlist, faltar uma necessária, não estiver presa só a este bucket e ao prefixo `duora/`, ou se o endpoint gravado
  não for o da conta. O resto do verify (versionamento `Enabled`, lock `COMPLIANCE` ≥ 21 dias) segue pela API S3.
- **Bucket:** File Lock na criação, retenção padrão `compliance` de 21 dias, lifecycle `daysFromHidingToDeleting: 30`
  nos prefixos `duora/` e `media/`, SSE-B2, privado. Todo bucket do B2 guarda versões.
- **pgBackRest:** `repo1-s3-endpoint=s3.us-east-005.backblazeb2.com` (número provável; **confirmar no bucket**, fica em
  `duora_b2_s3_endpoint`), `repo1-s3-region` = o segundo rótulo do endpoint, `repo1-s3-uri-style=host`, nome de bucket
  sem ponto. O arquivo é gravado por `duora-secrets write-backup-repo`, com a chave lida do stdin.
- **Filtro de saída:** os blocos IPv4 que a Backblaze publica (central de ajuda), conferidos contra o DNS na VPS. Eles
  liberam a Backblaze inteira, não só o nosso bucket; a contenção contra exfiltração continua sendo a cifra no cliente.
- **Limites novos em relação à AWS:** a chave do B2 não pode ser presa ao IP da VPS (vazada, funciona de qualquer
  lugar até ser apagada) e não há política de bucket que repita as negações para toda chave da conta: uma chave do
  outro projeto sem restrição de bucket e com `deleteFiles` apagaria versões do Duora fora do lock. O runbook manda
  conferir as chaves da conta (passo 2).
- **Custo:** armazenamento de poucos dólares por TB-mês e download gratuito até 3× o volume armazenado; o verify
  semanal completo fica perto dessa franquia no exemplo de dimensionamento
  ([backblaze-setup.md](../../operations/backblaze-setup.md#custo-esperado)).
- **Jurisdição:** a região fica nos EUA. O backup sai cifrado no cliente, mas a transferência internacional (LGPD)
  precisa ser registrada no mapa de dados com a salvaguarda escolhida; pendência de privacidade.

Compliance acrescentado: `B2KeyProblemsTests` e `B2BucketProtectionActionTests` (respostas no formato do B2:
capacidade proibida, chave sem restrição de bucket ou prefixo, região errada, 401, bucket sem File Lock, modo
`GOVERNANCE`) e `B2RepositoryPrefixTests` (o prefixo da chave é o `repo1-path` da imagem). A prova contra a conta real
(canário recusado sem `deleteFiles` e pelo lock) está no passo 6 do runbook e fica com o responsável.

## Adendo (2026-10-19): o servidor também fica nos EUA

Decisão do dono, registrada no [ADR-0026](0026-publico-idiomas-e-regiao-do-servidor.md): a VPS de produção fica em
Boston (EUA) e o público principal não é brasileiro. O item "Jurisdição" do adendo do B2 deixa de tratar o backup como o
único dado nos EUA: banco, fotos e backup estão todos lá (fato), inclusive os de pessoas no Brasil; se isso é transferência internacional (LGPD art. 33) ou coleta internacional (Resolução CD/ANPD 19/2024, a confirmar) e qual salvaguarda se aplica é a confirmar (J-02)
([data-map.md](../../privacy/data-map.md#local-do-tratamento-e-transferências-a-confirmar)). O backup continua sendo uma
cópia nos EUA, num provedor diferente do servidor. Nada muda na topologia, nas chaves nem no Object Lock.
