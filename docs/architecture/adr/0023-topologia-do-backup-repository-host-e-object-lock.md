# ADR-0023: Backup por repository host em container separado, mTLS e bucket com Object Lock

- **Status:** Accepted (2026-10-15, decisão do usuário)
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
