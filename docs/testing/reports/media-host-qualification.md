# Homologação S4 — roteiro de entrega ao operador

**Estado: preparado, não executado.** Este roteiro não ativa upload nem instala
serviços. Sua execução exige passagem explícita da janela ao operador designado,
com candidato final e ferramentas revisadas. P174-3/4 do ADR-0036 continuam
candidatos. #174, #86 e #69 têm critérios de encerramento próprios.

A VPS antiga foi aposentada como alvo. No host atual, exigir novo perfil, identidade,
recursos, pin e revisão próprios. Preservar provas antigas como histórico; não
reativar ou redirecionar seus scripts nem transferir aceite entre hosts.

## 1. Entrega e identidade do candidato

O integrador entrega um registro com os campos abaixo preenchidos com evidência
real, sem placeholders aceitos como prova:

| Grupo | Campos obrigatórios e vínculo |
|---|---|
| Fonte | Repository, commit final integrado, árvore, PRs/ancestrais, diff revisado e arquitetura do host |
| CI | Run/attempt/evento/branch e os nove jobs gerais, dois nativos e verificador da composição efetiva; identificação das condições não aplicáveis, sem contar skips como prova licenciada |
| Publicação | Run/attempt confiável da main exata, dois jobs licenciados e agregador concluídos; SBOM/licença/scanners do mesmo produtor |
| Pacote | Arquivo, tamanho, SHA-256 do arquivo, pin externo do manifesto, oito arquivos fechados e hashes internos, scope e arquitetura |
| Runtime | Referência OCI por digest, manifesto nativo, config digest, versão do launcher, protocolo e perfil da prova original |
| Instrumentação | Harness standalone e baseline host do mesmo produtor/fonte, hashes do kit e lista de arquivos, dependências/frameworks; sem importar `Duora.IntegrationTests` ou sua fixture PostgreSQL |

Rejeitar um pacote incompleto, misturado, expirado sem cópia preservada verificável,
de outra arquitetura ou sem correspondência com o SHA escolhido. As cópias antigas
podem demonstrar resultados históricos; não qualificam o candidato novo. Um ZIP
de Actions, um digest OCI e um pin de manifesto são identidades distintas.

Conservar o bundle e a prova de CI imutáveis. O perfil do host tem UIDs, socket e
journal próprios e precisa de pin separado: registrar sua derivação controlada e
os campos alterados, sem reescrever a prova sintética como se tivesse ocorrido no
host. Mesmo imagem/config digest e mesmas políticas não eliminam essa distinção.

## 2. Admissão operacional: HOLD antes de qualquer partida

O operador confere uma vez a autorização, o host/boot, a janela exclusiva e o
conjunto de recursos sintéticos. Mudança desde o snapshot invalida as comparações
afetadas e exige revisão pontual; não repetir inventário amplo sem necessidade.

- Fixar IDs/cgroups/unidades e ownership de todos os recursos do ensaio. Recursos
  legados, proxy, daemon compartilhado, volumes e backups ficam fora do seu alvo.
  PIDs históricos ou o primeiro `dockerd` encontrado não identificam recursos.
- Confirmar runtime rootless dedicado, API inacessível ao Worker/cliente, imagem
  pré-carregada e imutável, perfil root-owned e pin correto. O cliente recebe só o
  socket de controle necessário, kit somente leitura e diretório próprio limitado.
  Nenhum segredo, credencial de registry, storage ativo ou Docker API é montado.
- Validar framework/UID/permissões e recusa de configuração ausente ou divergente
  antes de iniciar o nativo. Não construir imagens ou binários na VPS.
- Conferir cgroup real de cada PID e seus ancestrais: memória, swap **max**,
  CPU/quota/período, PIDs, pesos CPU/I/O e cobertura do daemon/descendentes. Valor
  `swap.current=0` não comprova proibição de swap. O menor teto ancestral prevalece.
- Obter do responsável #86 contrato e cálculo do cenário simultâneo. Não somar
  sandbox/tmpfs novamente se já contidos no ancestral, nem omitir restic, launcher,
  auxiliares ou margem. Cálculo estático com enquadramento não é aceite operacional.
- Validar monitor/heartbeat, prazo externo e cleanup antes de carga. Guardas mais
  restritivas existentes prevalecem. Não diminuir limiar PSI para obter sucesso.
  Definir prazo da fase com margem de drenagem; as units históricas de 1.150/1.230 s
  não comportam a matriz longa de capacidade.

Registrar HOLD e motivo se qualquer controle estiver ausente, inconclusivo ou não
atribuível ao recurso correto. Não conceder acesso adicional como fallback.

## 3. Fases e critérios de prova

| Fase | Evidência necessária para prosseguir |
|---|---|
| Preparação sem início nativo | Monitor e medição completos, kit/perfil validados, lifecycle e limpeza comprovados, encaminhamentos observados independentemente; não marcar corpus ou capacidade aprovados |
| Corpus na Release isolada | 18 nomes únicos do corpus confiável, comparação de resultado/rejeição e três variantes com hashes/dimensões/metadados; relatório completo vinculado ao pin do perfil do host e ao candidato correto; sem fallback no Worker |
| Concorrência inicial limitada | Backup baseline concluído e freshness real; processamento e novo backup completos, intervalos úteis sobrepostos demonstrados pelos registros próprios; iniciar dois jobs ou observar overlap antes de abortar não basta |
| Capacidade C2 | Cliente real e protocolo do responsável #86 revisados; fases de aquecimento/rampa/pico/reconexão, repetições, critérios funcionais, recursos/PSI/throttling, filas/drenagem e backup em conjunto; não substituir por corpus, mock ou sleep |
| Recuperação e restauração | Restart/retry/fencing e ausência de efeitos parciais; destino sintético separado, backup verificado, SQL/objetos/invariantes/tombstones reconciliados, RPO/RTO medidos e plano de reversão seguro |

A matriz de falhas destrutivas/intentional OOM do runner descartável não deve ser
executada indiscriminadamente no host compartilhado com encaminhamento. Reutilizar
as provas atuais válidas do candidato e realizar somente falhas operacionais
específicas previamente revisadas, sem produto/backup simultâneo quando exigido.
Não executar `test_sanitizer_isolation.py` com identidade de runner forjada na VPS.

Os limites candidatos permanecem: metadados 4 KiB, variante 16 MiB, saída total
24 MiB e stderr descartado 64 KiB; sandbox 512 MiB/1 CPU/64 PIDs/tmpfs 16 MiB/sem
swap, decode 30 s, envelope externo 50 s e lease 120 s. Não alterar limites de
upload, allocator, segurança ou deadlines internos para acomodar o ensaio.
Recusa de saída legítima ou falta de margem exige decisão revisável.

## 4. Coleta: separar conclusão de processo e aceite

Guardar um diretório por execução, sem sobrescrever tentativas anteriores. Registrar
ID da tentativa, fonte, início/fim UTC e monotônico, host/boot, processo/unidade,
cgroup, imagem/config/pins, seed e dados sintéticos. Para cada fase, coletar:

- Estado completo/incompleto/abortado e código/motivo original, sem trocar falha
  por sucesso depois de cleanup; `worker.complete`, `measurement_complete`,
  `cleanup_complete` e `qualification_accepted` são afirmações diferentes.
- Série de recursos por cgroup e host com intervalo/denominador, anon/file/shmem,
  eventos OOM/high/max, swap, CPU/throttling, PSI e I/O. Picos cumulativos sem coorte
  e última amostra PSI zero não explicam nem eliminam um disparo anterior.
- Corpus 18/18, resultados por caso, hashes/bytes/variantes; tempos completos de
  startup/transferência/término/validação e margem restante no lease para storage e
  commit. Um relatório de baseline do processo atual não comprova envelope externo.
- Backup full/diff/WAL/restic conforme a fase, rótulos novos, começo/fim, bytes,
  sucesso, archive/freshness e sobreposição real com processamento concluído.
- Sondagens independentes, transport errors/timeouts/HTTP e estado dos serviços
  preservados antes/depois; falhas históricas de sondagem não são apagadas pela
  calibração posterior. Não correlacionar duas amostras como causa comprovada.
- Resultado de cleanup, IDs/cgroups e recursos ainda existentes, restauração e
  dados necessários ao diagnóstico. Nenhum payload real, token ou log de segredo.

O operador entrega arquivos sanitizados e hashes ao dono #174 e ao responsável #86.
Cada um registra seu aceite por critério e lacunas. A publicação verde não é prova
do host, e a aprovação do corpus não é aprovação de capacidade de participantes.

## 5. Interrupção e reversão

Qualquer guarda, falha funcional, invariante, diagnóstico inconclusivo ou cleanup
incompleto impede o aceite da fase. Parar somente unidades, processos descendentes
e containers pertencentes à execução identificada, com prazo e confirmação de
término. Se ownership não puder ser provado, manter HOLD e solicitar avaliação do
operador; não usar kill/stop ou teardown global para tentar garantir limpeza.

Manter upload desativado e novas admissões do ensaio bloqueadas. Preservar journal,
backup, artefatos e evidências; não aplicar `down --volumes`, apagar backups ou
alterar proxy/encaminhamento para reverter. Mudança de perfil/launcher só pode voltar
a um conjunto anterior isolado, compatível e previamente verificado, depois de
provar ausência de tentativas ativas e conservar a recuperação/deduplicação durável.
Sem versão segura, manter processamento parado; decode no Worker não é rollback.

No fechamento da janela, comparar serviços legados, identidades/mounts, configurações
permitidas e sondagens com o preflight, registrar recursos próprios parados ou
retidos para diagnóstico e obter revisão do checkpoint. Não reativar upload como
efeito do encerramento. #174 só fecha com seu checklist completo; #86/#69 não são
encerradas por consequência e P174-3/4 não se tornam homologados implicitamente.
