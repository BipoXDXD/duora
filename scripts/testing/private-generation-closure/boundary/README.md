# Fronteira experimental de controles — #311, PR A

Implementa somente as tarefas 1–2 do desenho aprovado: proteção de nomes/conteúdo dos controles,
separação de capacidades e journal explícito recuperável em processos Linux descartáveis. O código
não participa do build do backend, do contrato `IObjectStore` ou de qualquer decisão SQL de coleta.
**Não habilita fechamento terminal, uploads ou admissão operacional.**

## Executar

Na raiz do repositório, com Docker Linux nativo arm64:

```sh
python3 scripts/testing/private-generation-closure/boundary/run.py --report-dir /tmp/duora-boundary-report
python3 scripts/testing/private-generation-closure/boundary/check_mutations.py
```

O runner constrói a imagem de ensaio e executa ambas as áreas e as três variantes. Grava build,
resultados, SHA/estado da árvore, hash das fontes experimentais, arquitetura do daemon, SDK e ID
imutável da imagem. Também é possível construir com o Dockerfile desta pasta e executar:

```sh
python3 -m unittest discover -s scripts/testing/private-generation-closure/boundary -p 'test_*.py' -v
```

O SDK 10.0.403 está preso a digest **arm64**; não considerar isso cobertura amd64. Python 3 (PSF),
instalado somente na imagem de ensaio, usa a biblioteca padrão. Não há pacote novo na aplicação.

Cada fixture cria volumes nomeados exclusivos e remove somente seus próprios recursos em `finally`.
Nenhuma mídia, segredo, configuração operacional, Docker socket ou namespace de PID do host é montado.
O único bind é esta pasta de scripts, somente leitura. Rede desativada, raiz da imagem somente leitura,
`no-new-privileges`; os atores são sem capabilities efetivas. O provisionador tem apenas CAP_CHOWN
para preparar os volumes sintéticos. Ambos os volumes estão montados RW: as negações vêm do kernel.

## Capacidades e protocolo

| Ator da fixture | UID/GID | Capacidades concedidas |
|---|---|---|
| Legado | 1654 | Conectar ao socket; nenhuma escrita privada/controle |
| Cliente | 17020 | PUT com grant finito válido; sem fechamento/varredura |
| Autoridade confiável | 17010 | I/O real, journal, fechamento/varredura experimentais |
| Controlador confiável | 17030 | Journal da referência em segundo volume; somente read/close |
| Provisionador | 0 | Preparar e injetar falhas somente no universo sintético |

O pai dos controles é root:17010/0750 (referência: root:17030/0750); lock/journal são root:grupo/0660.
A autoridade grava o journal e bloqueia o inode, mas não pode remover, renomear ou chmod os controles.
Enrollment root:grupo/0440 vincula domínio/época/versão, reservas, donos/modos, dispositivos e inodes.
Ele é fornecido pelo provisionador confiável, nunca criado por um pedido. Abertura sem criação usa
`openat`, `O_NOFOLLOW`, `O_CLOEXEC`; `statx` verifica o descritor aberto, inclusive número de links.

Sockets 0666 permitem provar a autenticação real de peers recusados: a autorização usa UID/GID
obtidos do kernel por `SO_PEERCRED`, nas duas direções, e não campos do JSON. Os pais dos sockets
são provisionados sem escrita por clientes. O cliente não recebe grupo/capability de autoridade.
Frame JSON até 4 KiB; corpo até 98.304 bytes, tamanho exato; domínio, época, geração, área, nonce de
256 bits e variante são conferidos. Versão 1, caminhos livres, init/reserve e gerações comprometidas
são recusados. Não há API de alocação dinâmica nem prova de manifesto PostgreSQL.

Uma exclusão global pelo lock preexistente cobre validação, consulta da referência, toda escrita real
(inclusive EOF, dispose e rename), fechamento e varredura. Busy não quebra lock por prazo/PID/lease.
O servidor gera as chaves e compila por link o adaptador atual, sem reimplementar seu PUT.

O journal encadeia domínio, época, sequência, payload e hash SHA-256 com transições
`Reserved -> Open -> Closing -> Closed`. Não há estado implícito aberto: falta, corrupção ou cauda
incompleta requer recuperação, sem truncate, reparo permissivo ou bootstrap automático.

Fechamento ordena `Closing + Flush(true) -> revogação idempotente sincronizada pelo controlador ->
Closed + Flush(true) -> resposta`. Retry de registro existente sincroniza novamente antes do ACK.
O controlador autentica a autoridade e mantém revisão estável por geração. Indisponibilidade ou
incerteza impede confirmação. O hash detecta corrupção acidental; não autentica uma autoridade
comprometida nem detecta rollback de todos os controles/referência.

Varredura exige Closed e revisão confirmada antes de cada sessão. Remove as três chaves e somente
os temporários `.<32 hex>.partial` correspondentes; sincroniza o diretório. Não usa idade, não toca
vizinhos/committed e preserva nomes desconhecidos. `SweptExperimental` não comprova ausência de
bytes arbitrários e jamais é ACK SQL ou preenchimento de `writer_settled_at`/`deleted_at`.

## Limites deliberados

Esta fixture admite reinício de processo na mesma época para estudar recuperação do journal.
**Não é uma regra de readmissão operacional.** Retirada de famílias/FDs já abertos, binários históricos,
política de lançamento do novo UID, aposentadoria de épocas e barreira de restore pertencem ao PR B.
Não basta chown, tirar um mount ou consultar a referência para revogar descritores já abertos.

O controlador é um processo/volume local separado, não o ledger externo #69/#173. Root, provisionador,
autoridade e controlador íntegros são hipóteses. Um administrador pode restaurar bytes; rollback
integral da referência não é detectável por esta fronteira local. SIGKILL confirma morte real do
processo de ensaio; não simula energia, reboot do host, crash da VM ou garantia do dispositivo.
S3/B2 e filesystem remoto continuam sem qualificação específica.

O baseline do #316 permanece executável, incluindo os quatro contraexemplos. A exclusão
`boundary/**/*.cs` no projeto pai apenas evita que a inclusão recursiva do SDK misture os dois
executáveis. Nenhum teste anterior foi removido ou enfraquecido.

[Evidências e próximos gates](../../../../docs/testing/reports/filesystem-generation-boundary.md),
[ADR Proposed](../../../../docs/architecture/adr/0045-protecao-dos-controles-de-geracao.md),
[baseline](../README.md).
