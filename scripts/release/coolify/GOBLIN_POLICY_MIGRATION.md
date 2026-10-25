# Política administrativa do executor GoblinShift

Esta mudança é uma fonte candidata. Não instala arquivos, reinicia serviços, concede uma janela
de produção nem modifica os envelopes históricos. O protocolo continua 2; cada pedido exige o hash
exato dos bytes do receptor instalado, sem aceitar um conjunto arbitrário de hashes antigos e novos.

## Contrato da fonte

A política é exclusivamente `/var/lib/coolify-release/goblinshift/executor-images.json`.
Todos os diretórios são percorridos a partir de `/`, com descritores fixados, `O_DIRECTORY` e
`O_NOFOLLOW`: UID/GID 0, sem escrita de grupo/outros. A folha é regular, UID/GID 0, modo0600,
um único hardlink e no máximo64KiB. JSON tem somente `allowed`, não vazio, com referências imutáveis
válidas. Não há fallback, dual-write, migração no startup, criação de política vazia ou confiança emUID1001.

O receptor mantém os descritores e revalida os vínculos dos diretórios durante o import/deploy.
A configuração root-owned `/etc/goblinshift/executor-ctl.json` deve apontar para o caminho novo
antes do import, intent de import, alteração do Coolify ou start. Mudança concorrente dos arquivos
fixados interrompe a operação. Esta checagem do arquivo não prova qual configuração o daemon ativo usa.

O único escritor do fluxo de publicação é o ingresso administrativo sob as travas existentes do host
e do projeto. Ele preserva todos os digests aprovados anteriores e acrescenta o novo executor.
O temporário tem nome aleatório, criação exclusiva/nofollow e modo0600; escrita/fsync e rename usam
o mesmo dirfd, seguidos de fsync do diretório. Um `.next` ou temporário alheio não é seguido/apagado.
Falha antes do rename mantém a política anterior. Falha de fsync após rename deixa estado incerto:
preservar intents/evidência e reconciliar administrativamente; não restaurar/reenviar automaticamente.

## Entradas obrigatórias para a migração administrativa

1. Mesmos bytes/versionamento do receptor e metadata nas fontes e senders de Duora, GoblinShift e
   StillOn; suites próprias, revisão independente e prova nativa Linux da nova fonte. As CI válidas
   de componentes anteriores e o tar validado pelo receptor1ccd não qualificam o novo artefato.
2. Inventário atual, aprovado e identificado por hash, dos digests necessários a **todas** as tentativas
   aceitas/em curso e aos retornos aprovados. Incluir o executor89cec do currentcf67337; isso sozinho
   não prova que a lista está completa. Não usar o arquivo do pai1001 como autoridade por cópia cega.
3. Evidência atual de fila/intents/processos e drenagem do executor-ctl, respeitando tentativas aceitas.
   O administrador deve demonstrar que nenhuma transição cancela/perde uma tentativa. Se essa evidência
   estiver ausente, manter o ingresso fechado e não instalar uma lista parcial.
4. Pacotes anterior/candidato, configuração anterior e políticas preservados com hashes root-only;
   plano de retorno da aplicação/schema/configuração e das três imagens exatas já revisado.

Nenhum seed READY é fornecido sem o inventário de digests/tentativas completo. Nenhuma configuração
de participante, segredo, token de registry ou conteúdo de banco faz parte do pacote de fonte.

## Transição futura sob passagem exclusiva do operador69

1. Consumir o checkpoint atual e a passagem explícita. Congelar novos ingressos de publicação nos três
   projetos, reconciliar os intents existentes e aguardar imports/deployments anteriores terminarem.
   Preservar backups, assets, volumes, configuração regional/Resend, locks e histórico; não executar prune.
2. Revisar e registrar o conjunto aprovado completo, incluindo tentativas aceitas. Preservar o arquivo
   legado e sua origem como evidência; não mudar seu dono/permissões, transformá-lo em link ou apagá-lo.
3. Preparar o estado administrativo por procedimento restrito revisado: cadeia root-owned protegida,
   política semeada a partir do conjunto aprovado, escrita exclusiva e durável. Instalar os arquivos de
   configuração/leitor/receptor **já revisados**, sem editar fontes no host nem executar o instalador
   legado. O instalador novo recusa configuração/política ausente ou legada antes de qualquer mutação.
4. Depois da drenagem, fazer a transição controlada do executor-ctl. Registrar identidade do processo,
   fonte/versão e prova do leitor ativo usando o caminho e conjunto aprovados, com tentativas preservadas.
   Trocar somente o JSON em disco não cumpre este passo. Este documento não autoriza restart agora.
5. Sob as mesmas travas existentes, instalar/selecionar atomicamente o receptor compartilhado aprovado,
   conferir o hash instalado e as três cópias/senders correspondentes. Não instalar parcialmente nem
   aceitar hash divergente para liberar uma publicação. Qualquer publicação já iniciada com1ccd precisa
   terminar pelo contrato correspondente antes desta troca.
6. Qualificar a compatibilidade do novo receptor com os três grafos OCI de retorno existentes, em
   staging isolado e sem Docker/import. O novo envelope conserva commitcf67337 e os três digests;
   somente a identidade administrativa passa ao hash realmente instalado/qualificado. Preservar o
   envelope histórico914d/tarcfbf inalterados. Isso não autoriza dispatch do main antigo nem bypass do gate.
7. Registrar pós-condições da política, leitor ativo, receptor/senders, tentativas e runtime preservado.
   Somente após aceite independente reabrir o ingresso coordenado. Publicações de produto continuam
   exclusivamente pelo fluxo aprovado; a instalação administrativa não é um deploy de produto.

## Retorno e recusa

O retorno das imagens da aplicação conserva o novo plano administrativo e a política aprovada completa;
não reverte automaticamente para o escritor/arquivo sob pai1001. Reutilizar app46/web919/executor89
exatos exige envelope compatível com o receptor realmente instalado e prova da configuração/dados/schema.
Nenhum rebuild substitui esses digests e nenhum restore integral de snapshot é proposto.

Se a migração administrativa falhar antes de publicar seu commit, parar e preservar todos os arquivos,
intents e identidades: manter a aplicação e tentativas existentes; não reabrir ingresso com configuração
mista ou política vazia. Se for necessário retornar o plano administrativo inteiro, uma sequência própria
revisada deve reconciliar processos, tentativas e senders. Não há rollback seguro já demonstrado para
reabrir a escrita no caminho legado; isso permanece bloqueado, sem downgrade automático do receptor.

## Qualificação necessária

Os testes leves do receptor cobrem ordem da recusa, preservação, conflito, colisão e durabilidade
sintética. O teste `native_goblin_policy_proof.py` é executado explicitamente como root somente num
runner Linux descartável hospedado pelo GitHub; não é pulado silenciosamente pela suíte portátil.
Seu código testa permissões/UIDs/inodes reais e não prova transição do daemon ou preservação na VPS.
Os resultados nativos, qualificação do head final e aceitação operacional devem ser ligados a hashes
concretos antes de instalar. Esta entrega não afirma que essas operações foram executadas.
