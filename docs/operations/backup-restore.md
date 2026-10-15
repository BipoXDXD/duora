# Backup e restore do PostgreSQL

Backup físico com pgBackRest 2.59.3, arquivamento contínuo de WAL e restore PITR do Duora (spec 18, 12.4, 15.4 e
19.1). Deploy e rede: [deploy.md](deploy.md). Infraestrutura: [infra/README.md](../../infra/README.md).

**Metas (spec 18.1, propostas, não SLA):** RPO de até 5 min e RTO de até 2 h para serviço seguro restaurado. O
drill local mede os dois em cada execução (abaixo); a prova com o volume real depende da VPS.

## Arquitetura

```
             rede data (internal)                  rede pgbackrest (internal)        rede backup-egress
web, worker ─────────────── postgres ──── WAL por TLS (8432) ────▶ backup ─────── HTTPS ───▶ repositório S3
migrator                    │ archive_command                       │ pgbackrest server            (externo)
                            │ (archive-push assíncrono)             │ PGDATA e socket só leitura
                            └── volume pgdata, socket ──────────────┘ credenciais S3 + cifra
```

| Container | Redes | Tem | Não tem |
|---|---|---|---|
| `postgres` | `data`, `pgbackrest` (ambas `internal`) | `archive_command` do pgBackRest, certificado de **cliente** TLS | saída para fora, credencial do S3, senha de cifra |
| `backup` (repository host) | `pgbackrest`, `backup-egress` | `pgbackrest server` (TLS, exige certificado de cliente), credenciais do S3, senha de cifra, PGDATA e socket **só leitura** | porta publicada, senha de banco |
| `restore` (job, profile `restore`) | `backup-egress` | o mesmo do backup, com o PGDATA **gravável** | servidor TLS; só existe durante um restore |
| `web`, `worker`, `migrator` | `data` (e `edge`/`egress`) | nada do backup | credencial do repositório, socket do Docker, acesso a arquivo ou programa do servidor pelo SQL (teste `test_app_cannot_read_server_files_or_run_programs`) |

### Decisão: repository host via TLS (decidido pelo usuário em 2026-10-15)

A primeira versão deste PR punha o postgres numa rede com saída para o S3. A revisão de segurança apontou
`network-egress-control-missing`: um banco com saída para a internet facilita exfiltração se o processo for
comprometido (spec 15.2). Opções avaliadas:

| Opção | Prós | Contras |
|---|---|---|
| **(a) Repository host via `pgbackrest server` (escolhida)** | O postgres fica só em redes `internal`, sem nenhuma rota para fora; credenciais do S3 e senha de cifra saem do processo do banco (um postgres comprometido não lê nem apaga o backup); o filtro de saída só precisa cuidar de um container sem SQL exposto | Um container a mais; CA e certificados próprios para gerar, guardar e rotacionar (825 dias); o restore usa um job separado |
| (b) Archive no postgres + filtro `DOCKER-USER` por IP do endpoint | Topologia mais simples | O banco continua com saída (mesmo filtrada) e com credencial que apaga o repositório; endpoint S3 resolve para IPs que mudam (CDN), então a allowlist por IP é frágil |

Variante adotada dentro de (a): o `backup` monta o PGDATA e o socket **só leitura** e faz os backups localmente,
em vez de um terceiro container agente pgBackRest junto ao postgres. O poder do `backup` sobre os dados é o mesmo
nas duas formas (o backup físico já carrega o banco inteiro, e o pg_backup_start exige papel privilegiado), com
um container e um par de certificados a menos. A mesma imagem (`duora-postgres`) roda os dois lados: o pgBackRest
exige versão igual no cliente e no servidor.

### Saída da rede `backup-egress`

O role Ansible `backup` instala `duora-backup-egress.service`, que cria a chain `DUORA-BACKUP-EGRESS` e a liga em
`DOCKER-USER` para a subnet fixa `172.30.20.0/24` (a mesma do `compose.prod.yml`; mudar nos dois juntos):

- permite DNS (53/udp e 53/tcp): o resolvedor embutido do Docker encaminha a consulta com o IP do container, então
  **o acesso ao repositório depende de DNS**; sem resolução de nome o backup e o archive param (e o verify alerta);
- permite HTTPS (443/tcp) só para `duora_backup_s3_cidrs` (inventário). **Enquanto o provedor não for escolhido a
  lista é vazia, o que libera a 443 para qualquer destino**;
- descarta o resto. O serviço é `PartOf=docker.service`: reaplica a cada restart do Docker. A rede é só IPv4.

O postgres não depende desse filtro: ele não está em rede nenhuma com saída. O drill prova isso (abaixo); o filtro
`DOCKER-USER` em si só pode ser provado num host Linux real (pendência).

## Configuração

| Arquivo | Onde | Conteúdo |
|---|---|---|
| `infra/images/postgres/postgresql.duora.conf` | imagem, `/etc/postgresql/` | `wal_level=replica`, `archive_mode=on`, `archive_command` do pgBackRest, `archive_timeout=60`. Incluído no `postgresql.conf` do PGDATA por `initdb/20-duora-config.sh` na primeira inicialização |
| `infra/images/postgres/pgbackrest.conf` | imagem, `/etc/pgbackrest/` | Lado do postgres: repositório remoto `backup` por TLS, `archive-async`, `archive-push-queue-max=4GiB`, spool |
| `infra/images/postgres/pgbackrest-repo.conf` | imagem, `/etc/pgbackrest/` | Lado do backup e do restore: S3, `aes-256-cbc`, retenção, servidor TLS (`tls-server-auth=duora-postgres=duora`) |
| `pgbackrest_repo.conf` | segredo do operador | endpoint, bucket, região, chaves (abaixo) |
| `pgbackrest_cipher.conf` | segredo do operador | `repo1-cipher-pass` |
| `pgbackrest_tls_*` | `duora-secrets init-backup-tls` | CA, certificado do servidor `backup` e do cliente `duora-postgres` |

### Segredos do repositório (operador, uma vez)

O provedor ainda não foi escolhido; nada aqui presume um. Num bucket dedicado, crie uma credencial com permissão
só no prefixo `duora/` (listar, ler, gravar e apagar: o `expire` do pgBackRest apaga). Em `/etc/duora/secrets/`:

```ini
# pgbackrest_repo.conf (0400, dono 999)
[global]
repo1-s3-endpoint=<endpoint do provedor>
repo1-s3-bucket=<bucket>
repo1-s3-region=<região>
repo1-s3-key=<access key>
repo1-s3-key-secret=<secret key>
# Só se o provedor exigir: repo1-s3-uri-style=path, repo1-storage-port=<porta>
```

```ini
# pgbackrest_cipher.conf (0400, dono 999)
[global]
repo1-cipher-pass=<openssl rand -base64 48, gerado FORA da VPS>
```

```sh
sudo install -o 999 -g 999 -m 0400 pgbackrest_repo.conf pgbackrest_cipher.conf /etc/duora/secrets/
```

A senha de cifra é gerada e guardada **fora da VPS** (cofre do responsável, com cópia de emergência): perder a VPS
com a única cópia da senha torna o backup inútil (spec 18.1, "cópia inutilizável sem a chave não resolve"). O app
nunca recebe esses arquivos: só os serviços `backup` e `restore` os montam.

### Certificados TLS

`duora-secrets init-backup-tls /etc/duora/secrets` (rodado pelo Ansible, uma vez) cria uma CA própria (EC P-256,
10 anos), o certificado do servidor `backup` (SAN `DNS:backup`, `serverAuth`) e o do cliente `duora-postgres`
(`clientAuth`), 825 dias, 0400 com dono 999. A chave da CA é descartada: ninguém emite certificado novo com ela.
**Rotação** (antes de 825 dias, ou se um container for comprometido): apagar os cinco `pgbackrest_tls_*`, rodar o
comando de novo e recriar `postgres` e `backup` (`docker compose ... up -d --force-recreate postgres backup`).

## Retenção: janela recuperável de 14 dias

Agenda (timers do host, UTC, com até 15 min de jitter): **full aos domingos 03:00**, **diferencial de segunda a
sábado 03:00**, WAL contínuo (`archive_timeout=60`). Retenção no `pgbackrest-repo.conf`:

- `repo1-retention-full-type=time` + `repo1-retention-full=14`: depois de cada backup o `expire` só remove um full
  se **outro full mais novo já tiver pelo menos 14 dias**;
- WAL: sem `repo1-retention-archive*`, o pgBackRest só apaga WAL anterior ao full mais antigo que restou;
- diferenciais: sem `repo1-retention-diff`, cada um vive até o full do qual depende expirar.

A conta, com fulls F nos domingos (idade em dias no momento logo após o full de hoje):

| Momento | Fulls no repositório | Full mais antigo | Janela recuperável (até agora) |
|---|---|---|---|
| Domingo, logo depois do full | 0, 7, 14 (o de 21 expira porque o de 14 já tem 14 dias) | 14 dias | 14 dias |
| Sábado seguinte | 6, 13, 20 | 20 dias | 20 dias |

Logo, em regime, a janela fica **entre 14 e 21 dias, nunca abaixo de 14**: qualquer ponto desde o fim do full mais
antigo é recuperável com aquele full (ou um diferencial posterior) mais o WAL contínuo. Antes de completar três
semanas nada expira. Se um full semanal falhar, o anterior fica mais tempo (a janela cresce; não encolhe).

Espaço no pior momento: **3 fulls + até 18 diferenciais + 21 dias de WAL**, comprimidos (zstd) e cifrados. Exemplo
hipotético para dimensionar: full de 2 GB, 200 MB/dia de páginas alteradas e 500 MB/dia de WAL → 6 GB de fulls +
até ~12,6 GB de diferenciais (cada um cresce ao longo da semana: 0,2 + ... + 1,2 GB por semana) + 10,5 GB de WAL ≈
29 GB. Refazer com os números reais depois de uma semana de produção (`duora-backup info`).

**Tombstones (spec 12.4 e 18.4):** um registro pode voltar de um backup com até 21 dias (mais o tempo de um
restore). A retenção dos tombstones de exclusão precisa ser **pelo menos 21 dias + margem aprovada** (sugestão: 28
dias), e o restore os reaplica antes de reabrir (passo 6).

**Quebra da cadeia:** se o repositório ficar fora do ar e o WAL pendente passar de `archive-push-queue-max` (4 GiB),
o pgBackRest descarta a fila para o PostgreSQL não parar por disco cheio. Sem PITR depois desse ponto até o próximo
full: rode `sudo duora-backup full` assim que o destino voltar. O verify a cada 15 min alerta muito antes.

## Operação

`duora-backup` (root, `/usr/local/sbin`) só aceita estas ações e não repassa argumento ao pgBackRest:

| Ação | Quem chama | O que faz |
|---|---|---|
| `stanza-create` | operador, uma vez | Cria a stanza `duora` no repositório |
| `full` / `diff` | timers `duora-backup-full.timer` / `duora-backup-diff.timer` | Backup; espera um deploy em curso terminar (mesmo lock, até 30 min) |
| `verify` | `duora-backup-verify.timer` (15 min) e `duora-deploy` | `pgbackrest check` (troca o segmento e espera ele chegar ao repositório), `info` sem erro, último WAL confirmado há ≤ 300 s (RPO), arquivamento não falhando, último backup há ≤ 48 h |
| `info` | operador | `pgbackrest info` |

Ninguém além do root chama o `duora-backup`: o executor de deploy não tem sudo para ele, e nenhum container tem o
socket do Docker. Falhas aparecem em `systemctl --failed` e em `journalctl -u 'duora-backup@*'`.

### Primeira configuração

1. Bucket e credencial no provedor; gravar `pgbackrest_repo.conf` e `pgbackrest_cipher.conf` (acima).
2. `ansible-playbook` (role `backup`): certificados, `duora-backup`, timers e filtro de saída.
3. Primeiro deploy pelo operador, **sem** verificação (a stanza ainda não existe):
   `sudo duora-deploy --manifest release.json --accept-missing-backup-check`. A flag não está no sudoers do
   executor de deploy: só um humano com sudo a usa.
4. `sudo duora-backup stanza-create && sudo duora-backup full && sudo duora-backup verify`.
5. Daí em diante o `duora-deploy` do CI verifica o backup antes de parar qualquer serviço e para se ele não estiver
   saudável.

## Restore seguro (spec 18.4)

Restaurar é decisão humana (spec 17.4), nunca automática. Os 10 passos, na ordem da spec:

| # | Procedimento | Como, hoje | Verificação |
|---|---|---|---|
| 1 | Isolar o incidente e escolher o ponto de recuperação | Classificar: perda de host, corrupção, exclusão acidental ou comprometimento. Ponto = instante UTC antes do dano (`pgbackrest info` mostra a janela) | Registrar a causa e o ponto escolhido |
| 2 | Ambiente limpo e rede restrita | Comprometimento: VPS nova pelo playbook, certificados TLS novos (`init-backup-tls`), credencial S3 nova; nunca reutilizar host ou chaves suspeitos. Edge parado até o passo 10 | Só `postgres`, `backup` e `restore` no ar |
| 3 | Restaurar backup e WAL até o ponto | Ver "Comandos" abaixo | Timeline nova (`select timeline_id from pg_control_checkpoint()`), log com `recovery stopping before ...` no ponto pedido, `pg_is_in_recovery()` = false |
| 4 | Subir a aplicação em modo de recuperação | **Pendência do backend:** não existe chave que desligue e-mail, pareamento, notificações externas e jobs destrutivos. Até existir, web e worker **não sobem**; o edge fica parado | Nenhum efeito externo antes do passo 10 |
| 5 | Revogar sessões e reavaliar chaves | Revogar todas as sessões e tokens no banco restaurado (pendência do backend: comando de revogação em massa); trocar Data Protection, senhas de banco e credenciais suspeitas | Nenhuma sessão anterior ao incidente aceita |
| 6 | Aplicar tombstones posteriores ao backup | Reaplicar o registro externo de tombstones (spec 12.4) a todo registro restaurado; falha fechada se o registro estiver indisponível (pendência: o registro externo ainda não existe) | Contas e objetos excluídos não reaparecem |
| 7 | Reconciliar efeitos externos | Outbox, e-mails, uploads e verificações etárias posteriores ao ponto: conferir recibos dos fornecedores e expirar o que não tem recibo, sem reenviar em massa. **Efeitos externos continuam desligados** até aqui | Nenhum envio duplicado |
| 8 | Ajustar estados temporais | Salas e rodadas vencidas expiram; convites atrasados não são enviados (regras do domínio) | Nada recomeça "de ontem" |
| 9 | Testes de integridade e autorização | Contagens, amostras de relações, bloqueios e projeções privadas; `test_db_privileges` contra o banco restaurado; `sudo duora-backup verify` (o banco restaurado volta a arquivar) | Tudo verde |
| 10 | Liberar tráfego e jobs aos poucos | Subir web/worker/edge pelo `duora-deploy` da release corrente; acompanhar erros, duplicação e atraso | Registrar RPO e RTO observados (spec 18.5) |

### Comandos (passo 3)

Na VPS, com os dois env files da release corrente (como em [deploy.md](deploy.md#operação-manual)):

```sh
S=/var/lib/duora
ID=$(sudo python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["releaseId"])' $S/current.json)
C="sudo docker compose -f /opt/duora/compose.prod.yml --env-file /etc/duora/duora.env --env-file $S/releases/$ID.env"

$C stop web worker edge                    # passo 2: nada fala com o banco nem com a internet
$C rm --stop --force postgres backup       # o backup monta o PGDATA só leitura e sai junto
sudo docker volume rm duora_pgdata         # só depois de decidir que o volume atual não serve (copie antes se houver dúvida)
$C --profile restore run --rm restore --type=time --target="2026-10-15 18:00:00+00" --target-action=promote restore
$C up -d --wait backup                     # o restore_command do postgres busca o WAL nele
$C up -d --wait postgres                   # recovery até o ponto e promoção; acompanhe com $C logs -f postgres
```

Sem `--type=time`, o restore vai até o fim do WAL disponível (perda de host). Para manter o volume atual e
restaurar só o que mudou, use `--delta` no lugar de apagar o volume.

## Drill local (prova automática)

`scripts/backup/restore_drill.py` sobe o `compose.prod.yml` real (postgres, backup e o job restore, com os mesmos
secrets, redes e volumes) mais `scripts/backup/compose.drill.yml`, que acrescenta um **MinIO só como dublê S3 de
teste** (nunca produção) e um host `internet` arbitrário na rede de saída do backup:

```sh
docker buildx build -f infra/images/postgres/Dockerfile -t duora-postgres:drill --load .
python3 scripts/backup/restore_drill.py --image duora-postgres:drill
```

1. **Isolamento**: o postgres não conecta no host `internet` (nem por nome, nem pelo IP) nem no S3; conecta no
   servidor TLS do backup; o backup conecta no S3; o postgres não enxerga credencial do repositório.
2. `stanza-create` e full pelo `duora-backup`; escrita das linhas `before`; marca o ponto; escrita das `after`.
3. **RPO natural**: tempo entre o commit das `after` e o WAL delas confirmado no repositório, só com
   `archive_timeout` (sem troca forçada).
4. `duora-backup verify`; destrói containers e volume; restore PITR pelo job `restore`; promoção.
5. Confere `before` = 100 e `after` = 0, timeline ≥ 2, e `duora-backup verify` de novo.

**RTO observado** = da destruição até os dados conferidos. Saída em JSON (`--report arquivo.json`); `--keep`
mantém o ambiente. O CI roda o drill no job `images` (amd64 e arm64).

| Data | Ambiente | RPO observado | RTO observado | Observação |
|---|---|---|---|---|
| 2026-10-15 | Docker Desktop, Apple Silicon (arm64), banco quase vazio | 55,3 s | 31,2 s | Prova do caminho, não do volume real: o RTO de produção depende do tamanho do banco e da banda até o S3 |
| 2026-10-15 | Idem, versão final do PR (com prova de isolamento) | 55,6 s | 13,4 s | O RPO natural fica perto do `archive_timeout` (60 s), dentro da meta de 5 min |

Limites do drill: o MinIO roda na mesma máquina (sem latência de rede real); o filtro `DOCKER-USER` não é exercido
(Docker Desktop não tem a chain do host); dados sintéticos.

## Alertas (spec 19.1)

| Sinal | Hoje | Falta |
|---|---|---|
| WAL atrasado ou arquivamento falhando | `duora-backup verify` a cada 15 min falha (unit em `failed`) | Destino de alerta fora da VPS (OTel Collector ou monitor externo lendo o estado das units) |
| Backup full/diff falhou | Unit `duora-backup@full`/`@diff` em `failed` | Idem |
| Fila de WAL no pg_wal | Aviso do pgBackRest no log do postgres | Métrica do tamanho do `pg_wal` com alerta antes de 4 GiB |
| Certificado TLS perto de vencer | — | Checagem de validade (825 dias) no verify ou no monitor |

## Pendências que dependem da VPS ou do fornecedor

- **Provedor S3**: escolher (com região e jurisdição compatíveis com a spec 12), criar bucket e credencial restrita
  ao prefixo, preencher `pgbackrest_repo.conf` e os CIDRs do endpoint em `duora_backup_s3_cidrs`.
- **Cópia protegida contra exclusão** (spec 18.3, "resiliência a comprometimento"): a credencial do `backup`
  precisa apagar para o `expire` funcionar. Falta uma segunda cópia com retenção administrada fora da VPS (bucket de
  outra conta com replicação, ou versionamento com lifecycle maior que 21 dias). Object lock no bucket operacional
  conflita com o `expire` e exigiria adaptar a política.
- **Prova no host real**: rodar o playbook, conferir a chain `DUORA-BACKUP-EGRESS` (`iptables -S DOCKER-USER`), uma
  conexão de dentro do `backup` a um destino fora da allowlist falhando, restore completo com o volume real e o RTO
  cronometrado (spec 18.5), e o ensaio mensal de perda de VPS.
- **Restore isolado semanal automático** (spec 18.5) num ambiente de recuperação restrito, com a cópia e os segredos
  removidos depois.
- **Alertas** fora da VPS (tabela acima).
- **Backend** (passos 4 a 6 do restore): modo de recuperação com efeitos externos desligados, revogação em massa de
  sessões, registro externo de tombstones.
