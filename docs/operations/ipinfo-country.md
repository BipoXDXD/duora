# Base IPinfo: preparação inativa

As etapas 7 e 8 da [issue #108](https://github.com/BipoXDXD/duora/issues/108) preparam componentes sem registrá-los
nos hosts. O leitor usa o fallback de reflexão no runtime JIT; NativeAOT/trimming não estão homologados. [ADR-0033](../architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md) é a decisão vigente.
Este documento não é autorização de operação. BR + 27 UFs continuam sendo o catálogo executável.

## Componentes e autoridade

`MmdbCountryLookup` lê somente país, localmente. Web mantém `DisabledCountryLookup`; não existe opção de configuração
que troque esse registro. `CountryDatabaseUpdateWorker` tem enabled=false por padrão e não consta de WorkerHost.
Instanciar esses componentes requer integração futura explícita. Nenhum lookup grava país/IP, perfil, preferência ou idiomas.

Operations controla `ops.jobs` (kind exclusivo), `ops.country_database_state` (ponteiro, geração, bloqueio, ciclo) e
`ops.country_database_versions` (SHA-256 e manifesto append-only). A migração é aditiva, preserva kinds anteriores e não
agenda execução. Claims de Participation/Retention permanecem restritos aos próprios kinds. Não há I/O externo sob locks de negócio.

O executor deve renovar lease durante trabalho. Perda de posse cancela a tentativa; o token é revalidado na admissão do
manifesto e na promoção. Um worker atrasado não confirma ponteiro, mesmo se o arquivo já existir. O consumidor lê geração
mais manifesto em um snapshot SQL e recusa refresh atrasado. Consulta local não acessa SQL nem rede.

Cada tentativa usa diretório privado exclusivo, removido ao terminar. O arquivo final é `<sha256>.mmdb`, imutável; a
publicação segue manifesto durável → arquivo completo → ponteiro confirmado. Restart antes do ponteiro não ativa candidato.
Restart após commit exige arquivo e manifesto íntegros; ausência/corrupção mantém indisponível. Um leitor anterior já carregado
pode continuar somente enquanto admissível. Redownload conserva datas originais; arquivos existentes são conferidos, nunca sobrescritos.

## Pré-requisitos antes de qualquer ativação

- G2: homologar acesso autorizado ao country.mmdb real, formato e tipo exato, epoch confiável, schema, cadência e limites
  do fornecedor. A biblioteca MaxMind.Db 5.2.0/Apache-2.0 já foi aprovada; isso não homologa os dados.
- Definir explicitamente MaxBytes, Timeout, MaxNetworks e hosts HTTPS permitidos. Não há defaults operacionais.
  O perfil sintético dos testes não pode ser usado como configuração real. Redirecionamentos são limitados a três;
  Authorization só é enviado à origem fixa IPinfo, nunca a destinos redirecionados. Homologar autenticação e proteção das URLs assinadas.
- Validar o primeiro snapshot e aprovar seu hash. Atribuir IPinfo e preservar CC BY-SA 4.0 nos dados derivados/retidos;
  o leitor usa licença Apache-2.0, distribuída separadamente em `licenses/MaxMind.Db`.
- Verificar armazenamento privado compartilhado e coerente, leitura no Web e publicação no Worker; ACLs, capacidade,
  integridade, isolamento de escritores e backup separado. Diretórios existentes públicos são recusados. Nenhum mount,
  segredo, assinatura ou gasto foi provisionado. No Windows, ACLs precisam de homologação própria antes de qualquer uso.
- Verificar sincronização e confiabilidade do relógio em todos os processos e após restart. Datas futuras/ausentes são inválidas;
  recuo observado no leitor bloqueia a sugestão até o relógio alcançar a marca observada. Um host reiniciado com relógio
  incorreto não pode ser liberado sem verificação externa; não existe serviço de atestação de tempo nesta entrega.
- G3: definir titular, substituto, canal de alertas, cobertura de atendimento, serviço e orçamento de armazenamento.
  **Responsáveis nominais e canal permanecem pendentes.** G5: ensaios e autorização própria de implantação.

## Agenda e falhas

A linha recorrente agenda 06h America/Sao_Paulo. Restart após dias perdidos executa apenas o ciclo mais recente.
São no máximo quatro tentativas por ciclo: inicial e retries +15min, +1h, +4h em relação à primeira falha, nunca antes
agora/Retry-After. Rede, timeout, HTTP429/5xx e disco são falhas limitadas; 401/403, schema, integridade e falta de aprovação
bloqueiam a atualização para revisão humana. Regressão de epoch ou mudança de produto/schema/licença não é aceita automaticamente.

Epoch da construção determina idade: 72h requer aviso, 168h recusa leitura/promoção, mesmo sem atualizador em execução.
Download e restauração não rejuvenescem o arquivo. Não há dados de participante no manifesto nem detalhes de exceção,
resposta HTTP, token ou URL assinada nos diagnósticos. Falha de limpeza gera somente aviso sanitizado.

## O que ainda não está implementado

Etapa 9: monitor independente (36h sem execução, idade/falhas), incidentes deduplicados, retenção de até 14 versões/14 dias,
registros por 90 dias, cópia separada e recuperação administrativa auditada. Sobras de crash/limpeza exigirão reconciliação.
A role da aplicação não pode UPDATE/DELETE/TRUNCATE manifestos; retenção futura exige permissão específica revisada.
Bloqueio terminal não possui botão/comando de liberação automática; não contornar por SQL improvisado.

Não declarar cumprimento de retenção, recuperação em 15 minutos ou prontidão operacional com esses itens abertos.
Para rollback binário futuro, desligar atualizador/detector, conservar schema aditivo e dados; Down é recusado.
Aplicar migração e habilitar serviços exigem revisão humana e autorização separada. A #108 permanece aberta e US pertence à #78.

## Fontes primárias

- [MaxMind.Db 5.2.0](https://www.nuget.org/packages/MaxMind.Db/5.2.0) e [licença do leitor](https://github.com/maxmind/MaxMind-DB-Reader-dotnet/blob/v5.2.0/LICENSE).
- [Formato MaxMind DB](https://maxmind.github.io/MaxMind-DB/).
- [IPinfo Lite e exportações](https://ipinfo.io/developers/ipinfo-lite-database): não substitui a homologação de country.mmdb.
