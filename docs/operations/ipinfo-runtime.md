# Integração operacional IPinfo

Ver ADR-0038 e o runbook `ipinfo-country.md`. Os serviços são opt-in. Este documento não comprova que foram
instalados ou ativados; a evidência deve identificar release/digest, configuração sanitizada e resultado real.

## Configuração

Flags independentes em `CountryDatabase`: `DetectorEnabled` na Web, `UpdaterEnabled`, `MonitorEnabled` e
`RetentionEnabled` no Worker. Todas false por padrão. Configuração inválida com componente ligado falha no
startup, sem criar job/rede silenciosamente. Desligar detector volta a DisabledCountryLookup. Desligar updater
não impede o monitor de detectar idade/ausência de execução. Desligar monitor não aumenta validade do leitor.

Perfil medido em22/10/2026: `DatabaseType=ipinfo bundle_location_lite.mmdb`, `MaxBytes=67108864`,
`MaxNetworks=10000000`, `DownloadTimeout=00:03:00`, `RedirectHosts:0=dl.assets.ipinfo.io`. São valores explícitos
propostos a partir do ensaio, não defaults. Primeiro `ApprovedInitialHash` precisa corresponder ao artefato
validado e aprovado; não usar hash antigo se o fornecedor já publicou outra base. `Root` deve ser caminho
absoluto privado, montado na mesma posição Web/Worker, sem backup geral duradouro. Leitura Web somente leitura;
Worker dono da publicação. Diretórios públicos continuam rejeitados pelo publicador.

Intervalos configuráveis limitados: refresh1min, monitor5min, retenção30min; lease5min com renovação. Agenda
persistente do updater continua06h America/Sao_Paulo e retries limitados existentes. O leitor aplica168h em
cada consulta e conserva a versão anterior somente enquanto admissível. Relógio/geração/revogação permanecem
autoritativos. Revalidação de disco pelo monitor é independente do cache do leitor.

Segredos por arquivos em `/run/secrets`, nomes com `__`:

- `CountryDatabase__IpinfoToken` somente no Worker.
- `CountryDatabase__Gmail__Address` e `CountryDatabase__Gmail__AppPassword` somente no Worker.
- `CountryDatabase__Archive__AccessKeyId` e `CountryDatabase__Archive__SecretAccessKey` somente no Worker.
- `CountryDatabase__MaintenanceConnectionString` somente no Worker, usuário `duora_geo_maintenance`.

Gmail remetente/destino é o endereço privado autorizado, sem publicá-lo no repositório. SMTP fixo
`smtp.gmail.com:587`, STARTTLS obrigatório, limite30s. Esse canal é exclusivo de alertas operacionais da base;
não altera o e-mail transacional de participantes. Endereço/senha/token não aparecem em ToString de opções.

`Archive` exige ServiceUrl HTTPS, Region e Bucket explícitos. Bucket dedicado privado sem Object Lock,
replicação ou cópia geral. Chave limitada a `ipinfo-lite/`, list/read/write/deleteFiles; proibido Web escrever.
Validar política e apagar versões/marcadores/multipart no B2 real antes de operar. Lifecycle é defesa extra,
não substitui retenção explícita14 versões/14dias e metadados90dias. Backups de participantes ficam intactos.

## Role de manutenção

Criar administrativamente `duora_geo_maintenance` LOGIN sem superuser/CREATEROLE/CREATEDB/BYPASSRLS. Conceder
CONNECT no banco, USAGE em ops; SELECT/INSERT/UPDATE/DELETE somente nas tabelas `country_database_state`,
`country_database_versions`, `country_database_approvals`, `country_database_retired`,
`country_database_incidents`, `country_database_notifications`; SELECT/UPDATE em `ops.jobs` e uso da sequência
de notificações. Sem acesso aos schemas de participantes. Não herdar `duora_app` nem usar migrador.
Pool manutenção fixo2; `Database:MaxPoolSize` do Worker deve ser8 ou menos. Novo journal tem DELETE por cascade
somente na exclusão autorizada da notificação; a aplicação não pode apagar o pai nem o journal.
O pg_hba da imagem permite esse login por SCRAM somente no banco `duora`. A regra não cria o papel nem concede
acesso a tabelas; senha incorreta, outro banco e tentativa do app de assumir o papel continuam recusados.

## Entrega incerta

Journal `ops.country_database_deliveries` guarda apenas Id, attempted_at e accepted_at. Sem accepted_at, o envio
fica bloqueado para reconciliação, inclusive após restart. Não apagar a linha nem reenviar por palpite.
Message-ID: `<duora-country-ID-EPOCH_MILLISECONDS@meetduora.com>`, calculado com occurred_at do evento.
Conferir esse identificador exato no Gmail autorizado. Encontrado: operador registra aceitação no journal
com evidência/auditoria e deixa o monitor confirmar delivered_at. Ausente não prova que o SMTP não aceitou:
verificar canal, quarentena e período de entrega antes de decidir uma nova tentativa humana. Não prometer
entrega única sem esse limite. A fila conserva a ordem de avisos/recuperações enquanto houver evento pendente.
Após restore de banco, conciliar efeitos externos antes de liberar envios conforme INV-11.

## Instalação e reversão

Aplicar migração aditiva pela release aprovada antes de ligar serviços. Conferir NTP, volume privado, grants,
limites de memória/disco, acesso IPinfo e cópia fora do host. Ensaiar download→validação→promoção→refresh,
restart, base ausente/corrompida, restauração admissível sem rejuvenescimento e alerta/recuperação real.
Validar mensagens pelo Id e estado sanitizado; não consultar perfis, IPs ou preferências de pessoas reais.

Reversão: desligar componentes nas mesmas flags, publicar/reiniciar a configuração e conferir fallback manual;
preservar schema, manifestos e journal. Não fazer downgrade destrutivo nem reativar base expirada. A ativação
V3 de pareamento é independente e exige seu inventário de consumidores e ensaio próprios. US depende do
catálogo da release e da SPA coordenada; um resultado do detector não altera declarações/preferências.
