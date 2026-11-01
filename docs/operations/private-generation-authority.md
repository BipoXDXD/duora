# Autoridade de gerações privadas — candidato técnico #311

Desenho: ADR-0047. Nenhuma instalação, rollout ou ativação é autorizada por esta entrega.
Só Testing admite a configuração nova; Production, Staging, Development e nomes desconhecidos recusam.
O padrão é desligado. Fechamento terminal operacional de escritores incertos continua desabilitado.

## Fronteira e credenciais

Duora.StorageAuthority é um executável separado, com SO_PEERCRED nos dois sentidos. Web/Worker
recebem apenas socket, UID esperado e domínio/época/layout públicos. Conexões de referência e de
verificação de provas são arquivos protegidos lidos exclusivamente pelo host.

Enrollment root-owned vincula encarnação e identidades de dispositivos/inodes. Pais dos controles
não são graváveis pela autoridade/clientes. Não há criação de lock/journal ausente, quebra por idade
ou reset permissivo. Dados privados usam raiz física exclusiva, separada dos derivados legados.

A referência PostgreSQL usa banco e credencial próprios. Runtime só recebe EXECUTE nas funções de
admissão/transição, sem DML nas tabelas. Startup não inicializa referência/época. Sessão já consumida
não é readmitida automaticamente: nova época exige custodiante e prova de retirada da família antiga/I/O.

Dois bancos no mesmo cluster são apenas ensaio de restore lógico. Não protegem contra rollback físico
do cluster/host. Instância/custódia separadas, TLS autenticado, recuperação sem perda de ACKs e
qualificação operacional permanecem pendentes. Nenhum desses recursos foi provisionado aqui.

## Contrato e ordenação

Manifesto tipado fixa área/geração/variantes/hashes: até 16.777.216 bytes por variante e 25.165.824 por
conjunto. Sem paths livres; PUT genérico privado é negado no modo gerenciado.

Preparadores reais persistem manifesto e binding na transação de criação, antes dos bytes. Evidência
imutável do nascimento SQL impede reenquadrar registros antigos apenas atualizando xmin. Após commit
vêm Register, PutVariant e Seal; confirmação de negócio conserva suas guardas. Sem I/O de storage sob
locks de negócio. Seal congela o conjunto; não aprova uma foto.

Admissão do corpo é confirmada sob o gate antes do streaming. Gate cobre EOF, cópia, dispose, rename
e sincronização. Fila/conexões têm limite. Busy/timeout não provam encerramento.

Close revoga na referência sob a mesma exclusão, sincroniza Closing/Closed local e só então confirma
Closed externamente. ACK sucede todo I/O admitido; ainda não é remoção. Sweep exige Close anterior,
remove finais/temporários exatos, sincroniza diretório e grava prova SQL vinculada à posse corrente.

O papel comum duora_app não tem INSERT/UPDATE/DELETE/TRUNCATE de provas, nem EXECUTE no registrador
por PUBLIC. A função do verificador separado confere token/lease e binding. A migration feature-off
não cria role, LOGIN ou segredo. Eventual papel duora_storage_verifier depende de procedimento
operacional separado; o ensaio cria apenas credenciais descartáveis.

Cinco coletores de gerações executam Close→Sweep antes da conclusão gerenciada. Trigger SQL exige
prova para uma geração com binding mesmo se writer_settled_at estiver preenchido. Não fabrica ACK.
Uma prova descoberta durante DELETE antigo não torna essa passada terminal: é necessária outra
varredura completa após Close. Retenção de um ano/65 dias, relógio PostgreSQL e token/lease permanecem.

## Legado, rollback e restore

Default off mantém os caminhos existentes. No modo gerenciado, privateprefix sem binding é recusado
em leitura/exclusão mesmo se houver arquivo na raiz legada. Não há fallback. Quarentena e derivados
não privados continuam genéricos.

Consequência: corpus misto deixa leituras privadas legadas indisponíveis e obrigações em retry.
Não há migração transparente nem movimentação de arquivos nesta fatia. Uma transição futura exige
inventário/roteamento explícito, retirada de capacidades antigas e nova admissão; nunca retrofit
permissivo de metadados antigos. Esta indisponibilidade é deliberadamente fail-closed.

Rollback preserva referência, raiz, controles, épocas e negação de RW legado; cliente incompatível
fica indisponível. Não restaurar Open sobre autoridade ativa ou remover fences/provas. Sem Down destrutivo.

Restore exige staging sem leitura, retirada de famílias antigas e reconciliação com a referência e
ledger de exclusões atuais. Falta/corrupção/regressão bloqueia; não autoriza reset. Esta referência não
substitui o ledger externo de fotos da #69.

## Reprodução isolada

Na raiz da cópia isolada, com dados sintéticos e janela de recursos:

```sh
dotnet restore backend/Duora.slnx --locked-mode --disable-parallel
dotnet build backend/Duora.slnx --no-restore -m:1 /p:UseSharedCompilation=false
dotnet test --project backend/tests/Duora.IntegrationTests --no-build --no-restore -- --filter 'FullyQualifiedName~PrivateGenerationRoutingTests|FullyQualifiedName~PrivateGenerationProofPrivilegeTests'
python3 scripts/testing/storage-authority/run_linux.py --report-dir /tmp/duora-authority-evidence-novo
```

Relatório precisa de diretório novo. Runner usa Linux arm64 nativo com digest SDK10.0.402, não amd64.
Socket Docker local fica restrito ao runner Testcontainers autorizado. Não monta mídia, segredo real,
PID namespace do host ou volume operacional. Registra volumes anteriores e verifica preservação; sem prune.

Provisionador é root no runner. Host real roda em17010; cliente DI/preparadores/coletor em17020,
sem credenciais do host. Negativos1654/17020 exercitam syscalls. IDs apenas da fixture, sem atribuição real.
Registrar SHA/hash das fontes, imagem/arquitetura, comandos e resultados reais. Não atribuir os46 testes
antigos ao novo host. Aceitação explícita não foi adicionada aos workflows; todos os gates continuam.

## Limites operacionais

Sem homologação de energia/VM, hardware/filesystem-alvo, amd64, NFS/SMB ou S3/B2. Sem retirada de
escritores reais, prova de launcher operacional por digest ou custódia contra restore físico do cluster.
Rollback integral de toda a base confiável não é detectável por snapshot regressivo idêntico ao passado.
O candidato só é exercitável em laboratório. Timeout/cancelamento/heartbeat/PID/HEAD não substituem prova.

