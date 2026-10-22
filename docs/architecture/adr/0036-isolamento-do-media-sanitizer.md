# ADR-0036: Isolamento do decoder em media-sanitizer

- **Status:** Proposed
- **Origem:** [issue #174](https://github.com/BipoXDXD/duora/issues/174), [#69](https://github.com/BipoXDXD/duora/issues/69), [ADR-0025](0025-pipeline-de-fotos-imagesharp-s3-outbox.md), spec 12.3 e API-M01–M04.
- **Revisar quando:** Runtime, autoridade do launcher, protocolo, recursos, ameaças ou custos medidos mudarem.

Formalização da investigação de 21/10/2026, baseada na main `c6af25d53f9e5844e6ae92755ea3466504a673cb`.
As recomendações abaixo são candidatas, não decisões aceitas. Os responsáveis por função ainda não foram
nomeados e os itens P174-* permanecem pendentes. Aprovar ou integrar este PR documental não aprova o desenho,
não autoriza implementação e não satisfaz os gates de ativação.

## Relação com decisões vigentes

Propõe detalhar a futura isolamento do decoder hoje executado dentro do Worker, prevista como pendência no ADR-0025.
O ADR-0025 permanece Accepted e inalterado: este documento não o substitui enquanto Proposed. No aceite
futuro, identificar precisamente as partes substituídas e registrar referências recíprocas conforme as regras
do [índice](README.md#regras). A proposta complementar está no [ADR-0035](0035-revisao-humana-de-fotos.md).
O ADR-0034 e o contrato legado/opt-in já implementados permanecem vigentes; a política humana e o sandbox
não são uma continuação da migração de validação.

## Contexto e evidências

- [IPhotoSanitizer](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Core/Media/IPhotoSanitizer.cs) já separa entrada em memória e resultado `Sanitized`/`Rejected`. É a porta proposta para um adaptador remoto/local isolado, sem levar política editorial ao decoder.
- [WorkerHost](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Worker/WorkerHost.cs#L48) instancia ImageSharp no Worker. O [teste de arquitetura](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/tests/Duora.ArchitectureTests/DependencyRulesTests.cs#L57) hoje **exige** essa dependência no Worker. A futura extração precisa mudar essa sentinela para exigir o decoder só no novo executável; apagar o teste não é prova de isolamento.
- [PhotoSanitizerOptions/ImageSharpPhotoSanitizer](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Worker/Media/ImageSharpPhotoSanitizer.cs) define `MemoryLimitMegabytes = 256`, prazo cooperativo de 30 s, qualidade 80 e paralelismo interno 1. O limite do alocador não é teto de RSS do processo. O resultado usa `MemoryStream`/`byte[]` para cada variante; **não há quota explícita de bytes de saída/protocolo**. Não há um valor de saída existente a simplesmente copiar. Por isso, o critério original desta issue foi esclarecido para separar tetos existentes das quotas de transferência/saída ainda a definir.
- [Compose do Worker](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/infra/compose/compose.prod.yml#L247) configura secret de banco, montagens das duas áreas, redes data/egress e limites 1024M/1 CPU/256 PIDs. Um processo filho no mesmo container herda uma fronteira com rede/segredos/storage e não satisfaz o ADR-0025.
- [Dispatcher](https://github.com/BipoXDXD/duora/blob/c6af25d53f9e5844e6ae92755ea3466504a673cb/backend/src/Duora.Infrastructure/Media/MediaProcessingDispatcher.cs) lê até o tamanho limitado do original, confere SHA-256, chama o sanitizador fora de transação e só publica após checks de versão/commit. Defaults: lease de 2 min, 8 tentativas, 2 tentativas de desistência, janela técnica de 24 h. O boot só compara lease com duas vezes o timeout do sanitizador; um novo transporte precisa considerar também espera/startup/transferência/armazenamento.
- O original continua limitado a 5 MiB e 20 MP; variantes thumb/card/full têm lados máximos 256/768/1600 e não ampliam. Extração não altera esses limites, rejeições técnicas, bytes publicados intencionalmente, qualidade/configuração ou regra de consumo.

## Alternativas

| Opção | Benefício | Risco/custo | Parecer proposto |
|---|---|---|---|
| Processo filho dentro do Worker | Simples e permite encerrar um processo | Mesma fronteira de rede/montagens/segredos; não atende o gate | Descartar |
| Serviço persistente `media-sanitizer`, sem rede, troca por volume/pipe | Mais próximo de um serviço Compose; menor startup | Compromisso pode sobreviver entre jobs; cancelamento/reset e isolamento entre arquivos exigem supervisor externo confiável | Alternativa se demonstrar limpeza/reset e encerramento de toda a unidade entre tentativas |
| Container descartável por tentativa, perfil fixo, supervisionado fora do decoder | Fronteira nova por execução, descarte de memória/temporários e prazo duro externo | Introduz launcher operacional confiável e custo de startup; exige orçamento/homologação | **Recomendação para revisão**, condicionada ao aceite dessa nova autoridade |

Não escolher HTTP/TCP entre Worker e sanitizador: o requisito é container sem rede. A documentação oficial informa que [network none](https://docs.docker.com/engine/network/drivers/none/) cria somente loopback; ausência de portas publicadas ou rede bridge `internal` não é a mesma configuração.

## Decisão proposta, sujeita às decisões pendentes

1. Criar futuramente um executável de sanitização com a implementação ImageSharp atual; Worker usa um adaptador `IPhotoSanitizer` de protocolo fechado. Web, Infrastructure e Worker de orquestração não carregam o decoder. Reaproveitar os testes/algoritmos, preservando assinatura, animação, completude, custo estrutural, ICC/orientação, descarte de metadados e reencode.
2. Provisionar previamente imagem/digest e perfil de execução da mesma release. Um **launcher local dedicado**, fora do decoder e do Worker, controla criar/encerrar o container. Worker acessa somente uma operação restrita por socket Unix local protegido por permissões e autenticação local do chamador, sem listener IP; não recebe `docker.sock`, grupo Docker ou API genérica de runtime. Essa é uma dependência nova sujeita a revisão, não um componente que exista hoje.
3. O launcher recusa pedidos acima das cotas fixas do perfil; o orçamento informado só pode restringi-las, nunca ampliá-las. O launcher fixa imagem, comando, UID não root, rootfs somente leitura, capabilities removidas, no-new-privileges, perfil seccomp aprovado, rede none, memória/CPU/PIDs e temporários limitados. Não aceita do pedido nome de imagem, comando, variáveis livres, host paths, bind mounts ou flags Docker. Não monta banco, secrets, diretórios ativos de fotos, socket Docker, PID/rede do host ou área de outro job. Propor runtime dedicado/rootless quando suportado, **sem** mudar o daemon compartilhado do host por conta desta investigação.
4. Transportar original/resultado por streams com framing sobre stdin/stdout do container, mediados pelo launcher; stderr é separado, limitado e não vira resposta/log de conteúdo. O controle por socket Unix não adiciona rede IP ao container. Não usar um spool global RW onde o decoder veja arquivos de outras pessoas; qualquer arquivo temporário é por tentativa, restrito e destruído no fim.
5. Protocolo versionado: formato declarado, tamanho e SHA-256 calculados pelo Worker, nonce por tentativa e orçamento de execução; resposta fecha tipo de resultado, código técnico conhecido ou exatamente as variantes previstas. Não enviar ownerId, chave de storage, URL, credenciais ou dados de banco ao decoder. Nonce/hash correlacionam bytes/tentativa: **não autenticam a correção da sanitização**.
6. Antes de alocar/acatar saída, impor limites por frame/variante/total, rejeitar comprimentos excessivos, frames extras, duplicados, campos/versões desconhecidos, ausência de trailer/EOF esperado e resposta da tentativa errada. Validar dimensões/contagens/tipos e estrutura WebP/metadados com regras limitadas, sem introduzir novo decode no Worker. O sandbox não informa chaves de destino; Worker as gera e continua único escritor do storage.
7. Publicar somente depois do término confirmado do container, protocolo íntegro, checks de versão e commit usuais. Um `success` escrito antes de crash/timeout não basta. Manter slot de execução 1 no piloto; antes de admitir retomada, o launcher confirma que o job anterior terminou. Se isso for desconhecido, falhar fechado, não lançar outro decoder em paralelo às cegas.
8. Deadline imposto por supervisor fora do container, com encerramento da unidade inteira e confirmação de saída/limpeza. Prazo total inclui fila local, startup, cópia e validação; não prolongar silenciosamente o lease de 2 min ou timeout atual de 30 s. Determinar um envelope viável com medidas sintéticas e revisão, ou revisar a proposta antes de implementar.
9. Journal mínimo do launcher guarda só identificador opaco/estado/prazo/digest de release para dedupe e recuperação. Reinício limpa unidades/temporários órfãos antes de aceitar novo job; resultado antigo nunca satisfaz uma tentativa atual. Não armazenar originais, frames ou stack traces em journal/telemetria.

O [Docker documenta limites de memória/CPU e requisitos de suporte do kernel](https://docs.docker.com/engine/containers/resource_constraints/). É necessário provar os valores efetivos, inclusive política de swap e temporários; não confundir `MemoryLimitMegabytes = 256` com um container inteiro de 256 MB. O runtime/CLR e buffers também consomem memória. A [documentação de segurança do daemon](https://docs.docker.com/engine/security/) fundamenta tratar o launcher como autoridade sensível; um wrapper genérico de argumentos não resolve esse risco. [Rootless](https://docs.docker.com/engine/security/rootless/) é uma opção a verificar no host, não comprovação de enforcement existente.

## Classificação proposta de falhas e efeitos

| Evento | Tratamento a validar na implementação |
|---|---|
| `Rejected` técnico válido do sanitizador | Preservar código/rejeição já existentes e a limpeza do original |
| Prazo ou orçamento de memória do decode atingido | Preservar a semântica atual de `PROCESSING_LIMIT`; distinguir limite do decode de indisponibilidade do launcher |
| Falha de inicialização, launcher indisponível, crash ou protocolo inválido | Falha técnica com retry limitado pelo dispatcher; esgotamento segue falha terminal existente, nunca Approved |
| Shutdown do Worker | Propagar cancelamento e encerrar sandbox; lease/retry segue o protocolo atual, sem transformar shutdown automaticamente em rejeição de conteúdo |
| Resposta tardia/versão perdida | Descartar; nenhuma escrita/publicação/ACK por uma tentativa antiga |
| Banco/storage falha depois do decode | Manter retry/fencing e limpeza idempotentes; não publicar por existir arquivo parcial |
| Não é possível comprovar término/isolamento | Não admitir processamento seguro; manter upload desligado/bloqueio operacional até correção |

Nenhum valor HTTP ou código novo é decidido aqui. A análise não prova que um hash, container ou parser estrutural detecte todo comprometimento do decoder; saída continua não confiável, e escape do runtime/kernel é risco residual a revisar. Não adicionar um segundo decoder no Worker como solução implícita.

## Decisões pendentes e responsáveis por função

| ID | Decisão a revisar | Recomendação / consequência | Aceite necessário |
|---|---|---|---|
| P174-1 | Persistente com reset ou descartável | Preferir execução descartável por tentativa; quantificar startup/custo | Engenharia + segurança/operação |
| P174-2 | Autoridade e runtime do launcher | Template imutável e API mínima; decidir host/dedicação/rootless e quem o mantém, sem expor daemon ao Worker | Operação + segurança |
| P174-3 | Protocolo e limites de transferência/saída | Streams fechados e quotas explícitas; valores ainda não fixados, pois faltam medidas de saídas válidas/pico de buffers | Engenharia + segurança |
| P174-4 | RAM/CPU/PIDs/swap/tmpfs e prazo total | Preservar controles atuais e dimensionar novo processo sem elevar tetos do upload ou mascarar orçamento do host | Operação/#86 + engenharia |
| P174-5 | Readiness e falhas de infraestrutura | Definir quando boot/readiness recusam configuração incompleta e como observar indisponibilidade; sem novos códigos de API por acidente | Engenharia/API + operação |
| P174-6 | Manifesto/release/rollback | Fixar compatibilidade de protocolo, imagem/digest, licença/SBOM e versão do launcher; escolher versão anterior já isolada para rollback | Engenharia/release + segurança |

As funções acima não estão nomeadas como pessoas. Nenhuma dessas escolhas foi aprovada nesta investigação.

## Entregas posteriores, validação e dependências

- Antes de implementar: resolver e registrar as decisões pendentes, obter os aceites indicados e atualizar este ADR para Accepted por revisão explícita. A publicação deste documento como Proposed não satisfaz esse gate.
- Fronteira/protocolo e executável: mover a implementação e sentinelas arquiteturais, adapter do Worker e rejeição de protocolo; sem mudar contratos HTTP/OpenAPI/tipos. O gate de contrato deve provar os artefatos iguais.
- Runtime/launcher: infraestrutura e manifesto próprios, testes do parser/API mínima, template não influenciável pelo pedido, lifecycle/dedupe/cleanup e falhas; revisão humana da nova autoridade antes de qualquer instalação.
- CI real em runners amd64/arm64: matriz T-16 nos bytes de derivados; tentativas de rede/DNS/storage/secret proibidas; OOM, deadline duro, SIGKILL, stdout/stderr excessivos, links/paths maliciosos quando houver arquivos, protocolo truncado/extra, falta de launcher, Worker restart e resposta obsoleta. Conferir banco/outbox com PostgreSQL real e contagens diretas, incluindo falhas antes/depois do commit; não depender só de mocks ou inspeção de YAML. Exigir nove checks e evidência sanitizada do SHA final.
- #86 continua aberta por capacidade/enforcement reais: recalcular o perfil incluindo launcher e sandbox, memória de buffers nos dois lados e backup concorrente, sem assumir que o orçamento anterior já cobre um serviço novo. Homologação no host/release escolhidos comprova recursos e contenção; CI de outro projeto não substitui isso.
- #173 é independente do decode: o sandbox não conhece aprovação humana nem promove assets. Ambos bloqueiam a #69 com backup/B2/restore/montagens/verify e autorização própria. Retenção/leitura/exclusão de candidatos é da entrega editorial, não do container descartável.
- Rollback só para versão com isolamento e protocolo compatíveis. Se não houver essa versão, manter upload desligado e interromper decode com segurança; não retornar ao ImageSharp dentro do Worker com dados reais.

## Consequências

- Positivas: limita a exposição de banco/storage/segredos ao decoder e descarta a unidade a cada tentativa.
- Negativas: introduz launcher confiável, protocolo e custo de startup/recursos ainda não medidos.
- Trade-off: menor persistência de comprometimento por job exige mais operação; container não elimina escape de kernel/runtime nem prova correção dos pixels.

## Segurança e operação

Os controles e procedimentos descritos acima são requisitos para trabalho futuro, não comprovação de sua
implementação. Manter `duora_media_review: ""`, `duora_media_backup_enabled: false` e a allowlist de Production
vazia. Não liberar TechnicalOnly em Production/Staging, nem alterar limites, políticas executáveis, API,
segredos, infraestrutura ou ativação neste PR. Dados reais, deploy e homologação requerem autorização própria.
A #69 continua bloqueada pelas duas entregas, pela #86 e pelos gates B2/backup/restore/montagens/verify.

## Compliance

A futura implementação deve produzir a evidência de testes e homologação descrita acima, com dados
sintéticos, revisão humana e nove checks obrigatórios no SHA final. O CI deste PR valida a alteração
documental contra as suítes existentes; não comprova uma política humana ou sandbox ainda inexistentes.
Não executar builds, testes, Docker ou VM no Mac do responsável. Resultados do CI pertencem ao PR; nenhum
resultado futuro é presumido neste ADR. Issues #173, #174 e #69 permanecem abertas.
