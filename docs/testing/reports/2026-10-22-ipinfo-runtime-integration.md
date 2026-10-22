# Integração runtime IPinfo — verificação em22/10/2026

Execução autorizada pelo titular, somente em segundo plano. Código isolado, fixtures inventadas e PostgreSQL/
MinIO descartáveis no servidor, separados de Duora produtivo. Container de compilação/teste com1 CPU/1536MiB.
Nenhuma consulta a dados de participantes, instalação de serviço produtivo ou ativação ocorreu neste ensaio.

## Evidência observada

- Registro de hosts: RED3/4 (nenhum worker geográfico habilitado era registrado), depois GREEN4/4.
- Journal SMTP: RED1/5 na proteção contra exclusão do pai; os outros quatro cenários já passaram. Correção
  revoga DELETE/TRUNCATE do pai e do journal e repete a proteção no reconcile de privilégios.
- Suíte geolocalização:118/118, depois120/120 com persistência de Backup e cache/revogação de geração.
- Revisão independente encontrou recuperação falsa de Backup em retornos sem cópia. Regressão com Worker real
  falhou antes da correção. Retenção agora retorna resultado explícito; só Completed limpa backup_failed.
- Verificação final:124 testes, zero falhas, zero ignorados, em1min02s. Inclui PostgreSQL real (claims, journal,
  concorrência, ambiguidade, privilégios), MinIO real (bytes/manifesto/atribuição, ACL privada, versões, marcadores,
  multipart e conteúdo adulterado) e as três regressões missing/corrupt/clock.
- SDK e runtime alvo: base real aceita também na imagem exata runtime10.0.13-noble-chiseled-extra fixada pelo
  Dockerfile, sem rede, mounts somente leitura,1CPU/768MiB.11,31s, pico114.946.048bytes. Hash permanece
  c79e2755a3820baa605aef79f05074b64df3fe95eedae0fd3ca4aec0530fa5c2. Não se altera idade original da base.

Comandos de teste executados no container remoto: `dotnet test --project
backend/tests/Duora.IntegrationTests/Duora.IntegrationTests.csproj -- --filter-class '*Geolocation*'`. Build usa
avisos como erros e os mesmos pacotes travados. Logs sanitizados ficam no workspace da tarefa; não há base real
nem credencial nos commits/artefatos de GitHub. Não houve build/teste pesado no Mac.

## Limites e prontidão

Gmail foi comprovado pelo próprio adaptador.NET no servidor com a credencial válida fornecida posteriormente:
SMTP aceitou a mensagem e uma consulta IMAP somente leitura confirmou seu Message-ID exato no INBOX.
O titular confirmou a revogação da credencial anterior. Seu teste incerto foi reconciliado separadamente;
ausência na caixa não foi tratada como prova de não envio. O novo teste tem identificador próprio e não
habilitou os serviços produtivos. Endereço privado e senhas omitidos.

CI completo anterior executou3220 testes:3217 passaram e três inventários falharam porque não incluíam a
tabela nova nem a revogação de DELETE da notificação pai. O inventário de tabelas e a allowlist explícita de
privilégios foram atualizados para conferir SELECT/INSERT/UPDATE sem DELETE em ambos; nenhum gate removido.
Nova verificação remota após a atualização:149/149 passaram, zero falhas/ignorados, em1min14s. Inclui toda a
suíte geolocalização e as classes AppRolePrivilegesTests e MigrationsTests. Filtros por classe repetidos no
runner xUnit; a primeira tentativa com alternância em uma única expressão não selecionou testes e não foi
contabilizada como sucesso.

Bucket de backup existente tem Object Lock21dias e não pode cumprir a política IPinfo14dias. Chaves locais de
provisionamento estão revogadas; chaves operacionais não podem criar buckets/chaves. Nenhum bucket, chave B2
ou gasto foi criado. A provisão precisa de credencial ativa para criar recurso privado separado, seguida de
ensaio de ACL/exclusão no B2 real. MinIO não homologa o fornecedor. Browser não foi utilizado nesta continuação.

CI completo, revisão humana da migração do journal e instalação continuam obrigatórios. G3/G5, cobertura/SLA,
recuperação com cópia válida, cadência/precisão e ativação produtiva não estão aceitos por este relatório.
Nenhuma tentativa de instrução não confiável foi detectada nos arquivos consumidos nesta continuação.
