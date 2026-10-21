# Isolamento das releases — revisão conjunta de 21/10/2026

Origem: [Duora #159](https://github.com/BipoXDXD/duora/pull/159) e
[GoblinShift #122](https://github.com/BipoXDXD/GoblinShift/pull/122), já integrados.
Escopo: ingresso administrativo compartilhado; INV-12/T-12, ADR-0014 no Duora.
Não altera APIs da aplicação, schema, Compose, recursos Coolify, workflow, credenciais ou política de rollback.
Merge e instalação continuam sujeitos à revisão humana.

## Achados e correções

| Achado reproduzido fora de produção | Correção |
| --- | --- |
| Travas por projeto e concurrency do GitHub não coordenam dois repositórios; ambos passam pelo mesmo preflight de disco | `host.lock` comum, não bloqueante, antes de receber arquivos e mantido até confirmar saúde. Quem perde deve repetir depois pelos gates normais; não há fila/retry implícito |
| Importação pode continuar no daemon após a morte do cliente; pendência de um projeto não bloqueava outro | Intent comum durável antes do primeiro import. Qualquer falha mantém esse intent; pendências antigas de qualquer diretório de projeto também bloqueiam o host |
| `DockerRootDir` podia estar em outro filesystem; 4 GiB comprimidos não limitavam expansão de camadas | Inventário administrativo de storage, orçamento por filesystem, limite agregado de expansão e recusa de compressão não suportada |
| Identidade Docker depende do backend: containerd pode usar manifesto como ID; store clássico usa config | Confere o grafo OCI, inspeciona config/manifesto e vincula o ID de runtime comprovado ao container; confirma referência imutável antes de configurar Coolify |
| Cópias do receptor podiam divergir sem vínculo com o executável instalado | Artefato versionado com SHA-256, comparação entre repositórios/instalação e protocolo v2 preso aos bytes do receptor |

O digest do manifesto continua a identificar a imagem transportada/escaneada. O config digest é obtido
do manifesto validado. O receptor inspeciona primeiro o config e, somente se o daemon responder 404, o manifesto. Aceita somente um ID de runtime igual ao
manifesto (store containerd) ou config (store clássico), conferindo também o descriptor quando presente.
Esse ID comprovado no daemon é usado para comparar `Image` do container. Erros de rede/servidor não acionam fallback. Depois de Coolify terminar, saúde `starting` tem até 120 segundos dentro do prazo da release; identidade incorreta e saúde falha continuam recusadas. Não se troca digest por tag mutável.
Referência: [OCI config/ImageID](https://specs.opencontainers.org/image-spec/config/).

## Concorrência e limites da garantia

Ordem: host → projeto → backup StillOn (quando aplicável) → deploy Duora existente.
Todas são travas não bloqueantes; mantêm-se as travas e gates existentes. Nenhuma trava é removida ou
substituída por apagar/recriar o arquivo de lock. SIGKILL/SIGTERM liberam flock, mas o intent continua.
SIG_HUP continua ignorado como antes. O backup StillOn continua protegido pela sua trava original.

A trava coordena **somente receptores que adotem este protocolo na mesma VPS**. Deploy manual,
webhook, outro receptor antigo, restart/upgrade do daemon, garbage collector e outro host não obedecem
à trava. Os limites de builds/filas do servidor Coolify complementam essa proteção, não a substituem:
[Manage Server](https://coolify.io/docs/core/infrastructure/servers/manage-server).
Suspender rotas antigas/automáticas duplicadas e evitar operação manual concorrente é requisito operacional.
A fila do recurso alvo continua sendo conferida antes de alterar sua configuração.

O intent comum impede concorrência após resposta perdida de Docker/Coolify, inclusive quando ainda não há
UUID de deployment. Ele é conservador: falhar antes de criar um deployment também pode exigir intervenção.
Timeout nunca autoriza apagar intent nem iniciar rollback enquanto outra operação externa puder continuar.

## Admissão de armazenamento

Além de `DockerRootDir` retornado pela API, o administrador deve manter
`/etc/coolify-release/storage.json` root-owned e sem escrita pelas contas de publicação:

```json
{"version": 1, "image_roots": ["/var/lib/docker", "/var/lib/containerd"]}
```

O exemplo só vale se esses diretórios existirem e forem os efetivamente usados. Inventariar `data-root`,
root do containerd/snapshotter e seus mounts; incluir caminhos customizados e filesystems compartilhados.
O receptor recusa inventário ausente/inválido. [Docker documenta](https://docs.docker.com/engine/daemon/)
que `data-root` não muda necessariamente o armazenamento do containerd.

O limite do transporte é 4 GiB. Gzip e tar sem compressão são aceitos; zstd e outros formatos são recusados
até terem um leitor limitado revisado. A soma dos bytes descomprimidos das camadas de **todos os roles**
não pode exceder 16 GiB. O preflight reserva 8 GiB por filesystem: staging exige 8 + 8 GiB;
imagens exigem 4 + 16 + 8 GiB (conteúdo comprimido e camadas); quando compartilham o filesystem,
a exigência total é 4 + 8 + 16 + 8 = **36 GiB**. Filesystems repetidos não duplicam o orçamento.
Imagens antigas continuam retidas; quando faltar espaço, a publicação para. Não há prune automático.

São margens de admissão, **não quotas nem reserva física**: metadados/inodes, amplificação do snapshotter,
backups, crescimento do banco e escritas de outros processos podem consumir o disco durante a release.
Monitorar bytes e inodes, validar quotas e folga reais na homologação e ajustar a política por revisão.
Arquivos `incoming-*` deixados por SIGKILL não são apagados automaticamente; só remover a pasta exata
após confirmar que o processo/import correspondente terminou. Nunca executar prune global ou remover volumes.

## Versionamento e instalação administrativa (produção não alterada)

`receiver-release.json` identifica a versão 2.0.2/protocolo 2 e o hash exato de `receiver.py`.
Alterar receptor requer nova versão/hash e PRs coordenados nas duas cópias. O teste de artefato roda
na suíte existente; não é necessária mudança de workflow. Para conferir ambos os checkouts:

```sh
python3 /caminho/duora/scripts/release/coolify/verify_artifact.py \
  --peer /caminho/GoblinShift/deploy/coolify/release
```

A comparação lê arquivos e termina com erro se houver bytes ou versões diferentes. A mesma ferramenta
pode conferir o diretório da versão instalada. CI de um repositório isolado não prova o estado remoto;
o hash também vai no pedido v2 e o receptor rejeita incompatibilidade antes de importar/configurar.

Checklist para o administrador, depois da aprovação dos dois PRs:

1. Congelar novos ingressos e aguardar todos os processos de receptores antigos, Docker imports e deployments
   terminarem. O receptor antigo não usa `host.lock`; adquirir a trava nova sozinho não basta na primeira troca.
   Conferir recibos pendentes e filas de todos os recursos. Não alterar serviços para fazer essa inspeção.
2. Conferir os artefatos dos dois commits aprovados pelo comando acima. Se StillOn ou outro checkout usa
   este mesmo executável, coordenar seu sender v2 e o mesmo hash **antes de reabrir o ingresso**. Sender v1
   será recusado; isso não derruba serviços existentes. Não ativar uma atualização parcial.
3. Adquirir `/var/lib/coolify-release/host.lock` sem recriar seu inode. Sob essa trava, revisar o inventário
   de storage, confirmar ausência de intents e guardar evidência da versão atual, hashes e entrypoints.
4. Instalar **os bytes aprovados**, sem editar no host, em diretório imutável root-owned
   `/opt/coolify-release/versions/2.0.2-<sha256>/`, com `receiver.py` e `receiver-release.json`.
   Diretórios e executável 0755, metadados 0644; nenhum pai pode ser gravável pelas contas de publicação.
   Conferir a versão instalada com `verify_artifact.py --peer <diretorio-da-versao>`.
5. Manter `/opt/coolify-release/receiver.py` como entrada estável apontando para a versão aprovada.
   Trocar o link por rename atômico sob a trava (fsync do diretório), preservando a versão anterior.
   Não mudar forced commands, sudo allowlist, recursos ou tokens incidentalmente. Revalidar propriedade,
   permissões, hash e compatibilidade dos senders antes de liberar novos ingressos.
6. Instalar primeiro em homologação descartável com a mesma versão/backend de Docker e Coolify. Validar
   load OCI, resolução offline de `tag@manifest`, saúde, ownership, mounts, backup e rollback compatível.
   Testes de produção não fazem parte deste procedimento de revisão.

Rollback do receptor: congelar ingresso, reconciliar intents, adquirir a mesma trava e repontar atomicamente
para os bytes anteriores revisados. Reverter senders junto se o protocolo diferir. Não reabrir releases
paralelas com a versão antiga; ela não oferece esta coordenação. Não apagar intents para efetuar downgrade.
Rollback do aplicativo continua pelo procedimento existente, com backup e schema compatível; não restaura
bancos, volumes nem versões de schema automaticamente.

## Reconciliação de falhas e estados pendentes

Operação exclusiva de administrador, fora do SSH restrito de CI; não existe endpoint de limpeza remota:

1. Congelar novos ingressos. Sob `host.lock`, localizar intent comum e intents por projeto; guardar cópias
   root-only com commit, UUID (se houver), horário e diagnóstico. Intent sem UUID pode corresponder a start
   aceito cuja resposta foi perdida: pesquisar a fila do recurso, não reenviar start cegamente.
2. Confirmar que nenhum processo/import Docker ou deployment está ativo/desconhecido, incluindo tarefas
   órfãs. Estado desconhecido permanece bloqueado. Não matar outros serviços/projetos para liberar o lock.
3. Comparar configuração, manifest Duora/predecessor, allowlist GoblinShift, imagens/config IDs, mounts,
   volumes, fila e readiness. Se terminou saudável, preservar recibo verificável no histórico antes de
   reconciliar. Se falhou, planejar nova tentativa/rollback compatível pelo runbook existente; não há downgrade
   de schema automático. Se `current.json` foi escrito mas o intent não removido, não repetir a implantação.
4. Somente depois de resolver os efeitos externos e guardar a evidência, arquivar por rename os intents
   exatos reconciliados (comum **e** do projeto) para diretório de auditoria root-only; fsync dos diretórios.
   Deixar `current.json`, histórico, versões anteriores, assets, bancos e volumes preservados.
5. Liberar a trava e novos ingressos apenas com estado conhecido. Repetir pelos gates atuais; commit
   superado continua recusado. Não acrescentar bypass de CI, limpeza global, restore ou retry automático.

## Testes reproduzíveis e limites desta revisão

```sh
python3 -m unittest discover -s scripts/release/coolify -v       # Duora
python3 -m unittest discover -s deploy/coolify/release -v       # GoblinShift
```

Usar Python 3.12+ no host. As suítes executam processos independentes, flock e filesystem reais com dados
sintéticos, simulando somente operações externas e caminhos administrativos. Cobrem dois projetos em disputa,
SIGKILL no import, SIGTERM durante start, resposta de start perdida, falha de deployment, pendência legada,
recusa antes de consumir entrada, ausência de falso sucesso, recuperação administrativa e preservação de
sentinelas de banco/volume/predecessor e da allowlist anterior. Os testes OCI exercitam blobs reais em disco,
expansão gzip, IDs distintos e referência imutável indisponível. Os testes de artefato recusam drift.

Sentinelas e simulações não comprovam integridade de PostgreSQL, isolamento efetivo de mounts, recovery após
queda da VPS nem durabilidade do daemon. Nenhuma VPS, dado, segredo ou publicação de produção foi acessado.
A homologação descartável com Docker/Coolify/PostgreSQL reais está em [HOMOLOGATION.md](HOMOLOGATION.md),
com versões, resultados, ensaios invalidados e limitações. Paridade com versões, schema, carga e backups
efetivamente instalados continua exigindo validação operacional própria antes da ativação.
