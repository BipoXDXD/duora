# Base IPinfo: preparação inativa

As etapas 7, 8 e 9 da [issue #108](https://github.com/BipoXDXD/duora/issues/108) preparam componentes sem registrá-los
nos hosts. O leitor usa o fallback de reflexão no runtime JIT; NativeAOT/trimming não estão homologados. [ADR-0033](../architecture/adr/0033-deteccao-aproximada-e-preferencia-regional.md) rege a arquitetura, com a troca de produto no ADR-0037 abaixo.
Este documento não é autorização de operação. BR + 27 UFs continuam sendo o catálogo executável.

## Direção vigente — IPinfo Lite (22/10/2026)

O [ADR-0037](../architecture/adr/0037-ipinfo-lite-mmdb-para-sugestao-de-pais.md) substitui a escolha de produto
anterior por IPinfo Lite `ipinfo_lite.mmdb` / CC BY-SA 4.0. O ADR-0033 mantém as demais decisões.
A adaptação inativa do [PR #183](https://github.com/BipoXDXD/duora/pull/183) troca explicitamente
produto/fonte/schema e leitura. O contrato é `ipinfo_lite.mmdb`, `ipinfo-lite-country-v1` e origem
`https://ipinfo.io/data/ipinfo_lite.mmdb`. Não admite `country.mmdb` por renomeação, metadados antigos ou fallback.
Os limites e o `DatabaseType` real continuam exigindo perfil G2 explícito, sem defaults operacionais.
No Lite, o código ISO é `country_code`, e `country` é nome. O Duora utiliza apenas a projeção de país;
ASN e demais campos não viram atributos de participantes, filtros ou logs por consulta. O MMDB bruto contém
campos adicionais e permanece sujeito a armazenamento privado e à retenção aprovada.

Seguir o [plano de homologação atualizado](../superpowers/plans/2026-10-22-issue-108-operational-homologation.md)
para obter aceite do perfil e primeiro hash Lite. Este runbook não atesta compatibilidade nem homologa a base.
Sem download ou ativação nesta mudança; detector/atualizador/monitor seguem desligados, sem EUA/alertas reais.

A validação percorre redes IPv4/IPv6 com limites explícitos e recusa código ausente, malformado ou não
reconhecido pelo mapeamento conservador BCL, tipos divergentes e campos fora do schema documentado. Campos
adicionais conhecidos permanecem strings opcionais ignoradas na projeção; o nome nunca é fallback para código.
A identidade completa do manifesto é obrigatória na construção. JSON sem produto/fonte/schema/licença não
herda a escolha Lite e não é admissível; cópias antigas não recebem novo rótulo ou aprovação implicitamente.
Os testes usam somente MMDBs inventados; não comprovam acesso, precisão ou compatibilidade de base real.

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

- G2: revisar a adaptação inativa Lite e homologar acesso autorizado ao `ipinfo_lite.mmdb` real, formato e tipo exato, epoch confiável, schema, cadência e limites
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
- G3: o responsável designou **a si como titular, seu Gmail privado como canal de alertas e nenhum substituto por enquanto**.
  [Decisão registrada em 21/10/2026](https://github.com/BipoXDXD/duora/issues/108#issuecomment-6327178280).
  Não publicar o endereço privado. Permanecem pendentes contingência/substituição, cobertura regional de atendimento,
  serviço e orçamento de armazenamento e comprovação de entrega dos alertas. A ausência de substituto é uma limitação
  expressa, não cobertura comprovada; G3 permanece aberto. G5: ensaios e autorização própria de implantação.

## Agenda e falhas

A linha recorrente agenda 06h America/Sao_Paulo. Restart após dias perdidos executa apenas o ciclo mais recente.
São no máximo quatro tentativas por ciclo: inicial e retries +15min, +1h, +4h em relação à primeira falha, nunca antes
agora/Retry-After. Rede, timeout, HTTP429/5xx e disco são falhas limitadas; 401/403, schema, integridade e falta de aprovação
bloqueiam a atualização para revisão humana. Regressão de epoch ou mudança de produto/schema/licença não é aceita automaticamente.

Epoch da construção determina idade: 72h requer aviso, 168h recusa leitura/promoção, mesmo sem atualizador em execução.
Download e restauração não rejuvenescem o arquivo. Não há dados de participante no manifesto nem detalhes de exceção,
resposta HTTP, token ou URL assinada nos diagnósticos. Falha de limpeza gera somente aviso sanitizado.

## Monitor independente e entrega inativa — etapa 9

`CountryDatabaseHealthService.RunOnceAsync` é invocação explícita, com `enabled=false` por padrão, independente
do atualizador. Não está registrado em Web/Worker e não cria timer, job ou automação de chat. Avalia ausência
de execução em **36h**, aviso de idade em **72h**, indisponibilidade em **168h**, falta do arquivo, tentativas
esgotadas e falhas sanitizadas de credencial/schema/integridade. O leitor aplica 168h mesmo sem monitor.
Incidentes ativos e notificações são persistidos em Operations, sem IP, país por pessoa, endereço de e-mail ou payload.

Primeira ocorrência, mudança de gravidade e recuperação geram evento; intervenção ainda pendente gera lembrete
a cada 24h, sem repetir por poll/restart. A fila reserva entrega por 15 minutos e processa até 32 eventos por
invocação. Eventos pendentes do mesmo incidente preservam ordem, inclusive após falha de envio; recuperação não
ultrapassa aviso/indisponibilidade ainda sem confirmação. `ICountryDatabaseNotifier` exige deduplicação pelo Id durável: uma queda depois do envio e antes do
commit pode repetir a chamada com o mesmo Id. Falha não registra detalhes do transporte e mantém retry limitado.
O único adaptador está nos testes; não há SMTP, Gmail API, credencial, endereço ou envio real. O Gmail privado
escolhido pelo titular continua sendo o destino autorizado, **não um canal operacional comprovado**.

## Retenção e cópia privada

`CountryDatabaseRetentionService` também vem desligado e sem agendamento. Aplica simultaneamente **14 versões**
e **14 dias desde o primeiro download**, inclusive à antiga ativa sem substituta. Validade de serviço permanece
inferior a sete dias desde construção; diagnóstico não renova validade. Redownload conserva manifesto original.
Primeiro confirma tombstones/revogação e invalida publicadores antigos; depois remove bytes. Falha de I/O exige
retry e reconciliação, sem reautorizar versão aposentada. Arquivos órfãos e diretórios `.attempt-*` são varridos;
diretórios de download em andamento são preservados enquanto houver lease válida. Rejeição normal remove bytes
ao fim da tentativa, mantendo apenas diagnóstico/manifesto. Crash exige a próxima varredura explícita.

Leitores usam cópia imutável em memória: `OpenVerified` fecha o handle de arquivo antes de disponibilizar o Reader.
Remover arquivo de disco não interrompe a consulta em andamento. Refresh respeita geração e revogação; a idade
é conferida em toda consulta. A publicação e a manutenção compartilham um advisory lock de sessão PostgreSQL
exclusivo, separado das transações/guardas de participação. Downloads e notificações ficam fora desse lock;
ele limita escritores, não consultas. Criação de staging também participa desse protocolo. O monitor não adquire
esse lock: avalia snapshot SQL e verifica geração antes de registrar, inclusive durante cópia externa lenta.
Espera de lock de escritores é limitada e uma falha exige retry pelo futuro orquestrador.

Manifestos, aprovações, tombstones e eventos saem aos **90 dias**; incidentes ainda ativos representam uma condição
atual: registros inativos expiram, a data de abertura é limitada à janela de 90 dias e só notificações recentes ficam. `duora_app` continua sem UPDATE/DELETE/TRUNCATE
em versões, aprovações e tombstones. A retenção requer **data source de manutenção com privilégio específico**;
provisionar essa role, identidade e grants mínimos é pré-requisito de G3, sem reutilizar o migrador em produção.
Os testes usam o migrador somente no PostgreSQL descartável para simular essa capacidade e adulterações negativas.

`ICountryDatabaseArchive` prepara cópia da ativa/anterior aprovada, limitada a dois objetos privados e aos mesmos
prazos. Empates de epoch seguem a ordem durável de promoção, nunca a ordem lexicográfica do hash;
a ativa tem prioridade na cópia, junto da anterior distinta. O adaptador futuro deve reter manifesto original, atribuição IPinfo/CC BY-SA 4.0 e excluir todas as versões,
réplicas e uploads incompletos de cada chave removida; não incluir credenciais nem bases em backup geral duradouro.
Serviço, orçamento, limites, timeouts, ACLs e demonstração de exclusão/entrega permanecem pendentes. Só existe
adaptador sintético em memória nos testes; **nenhum armazenamento fora do host foi contratado ou provisionado**.

## Recuperação e liberação humana

1. Manter fluxo manual e serviços desativados durante investigação. Conferir relógio/sincronização externa antes
  de invocar recuperação; marca UTC persistida recusa recuo observado, inclusive após restart do serviço.
2. Recuperar somente hash com aprovação durável no registro autoritativo, nunca ponteiro/manifesto fornecido por
  backup antigo. Escolher a construção aprovada mais recente, dentro de 168h, sem tombstone, e conferir novamente
  hash, tamanho, manifesto original, licença/produto/schema, legibilidade e redes IPv4/IPv6. Nenhum acesso à IPinfo.
3. Arquivo local ausente pode ser restaurado da cópia separada admissível; datas não mudam. Não rebaixar para uma
  construção anterior só porque a cópia mais recente está ausente. Sem cópia admissível, continuar indisponível.
4. Corrupção de bytes/manifesto e bloqueios de integridade/schema/licença exigem revisão humana. O método
  `ReleaseAsync(expectedGeneration, reviewId, OperatorName, ...)` exige geração vigente e referência opaca não vazia,
  registra auditoria e invalida leases. Não existe endpoint, comando público ou liberação automática. O futuro
  adaptador operacional deve autenticar/autorizar o operador antes da chamada; OperatorName sozinho é rótulo,
  não prova de identidade. Uma liberação permite reparar arquivo, mas não serve bytes antes de nova validação.
   A autorização é consumida pela recuperação ou por qualquer promoção posterior; um incidente novo exige nova revisão.
   O monitor confere todos os campos/coerência do manifesto, inclusive origem, tipo e cronologia, antes de declarar saúde.
5. Trocar arquivo completo e confirmar ponteiro com fencing; não copiar credenciais, rejuvenecer download ou confiar
  em geração de outro backup. Manifesto/registro perdido exige reconstrução e homologação humanas.

A meta é **até 15 minutos com cópia válida**, a medir em ensaio real; testes sintéticos não comprovam esse SLA.
Atendimento aprovado: um dia útil para aviso, quatro horas **dentro da cobertura acordada** para indisponibilidade.
Cobertura, contingência e substituto seguem pendentes; não presumir plantão 24x7. Ensaiar trimestralmente e após
mudanças: ausência/corrupção, cópia antiga/adulterada, relógio incorreto, disco cheio e concorrência. Registrar duração,
hash, geração, categoria e resultado sanitizados, sem dados de participantes nem endereço privado.

G2 (base real/limites/relógios), G3 (capacidade operacional) e G5 (ensaio/ativação) continuam abertos. Não declarar
prontidão, retenção em produção ou recuperação em 15 minutos. Aplicar migrações, implantar, registrar serviços,
provisionar storage e habilitar transportes exigem revisão/autorização próprias. Down é recusado; rollback binário
mantém schema aditivo e serviços desligados. A #108 permanece aberta e US pertence à #78.

## Fontes primárias

- [MaxMind.Db 5.2.0](https://www.nuget.org/packages/MaxMind.Db/5.2.0) e [licença do leitor](https://github.com/maxmind/MaxMind-DB-Reader-dotnet/blob/v5.2.0/LICENSE).
- [Formato MaxMind DB](https://maxmind.github.io/MaxMind-DB/).
- [IPinfo Lite e exportações](https://ipinfo.io/developers/ipinfo-lite-database): fonte do produto planejado no ADR-0037; não substitui a homologação de G2.


## Homologação técnica e reversão — etapa 10

A [matriz integrada](../testing/regional-matching.md#homologação-integrada--etapa-10) e o
[roteiro de release](regional-matching.md#homologação-e-release--etapa-10) consolidam os testes sintéticos.
Não homologam arquivo real, precisão, acesso IPinfo, Gmail, storage, relógios entre hosts ou SLA de recuperação.
Não há registro de serviços, job operacional ou nova coleta. Manter detector/atualizador/monitor desativados,
sem enviar alerta real; a homologação operacional exige autorização própria e os gates G2/G3/G5 acima.
