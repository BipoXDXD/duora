# Controle local do sanitizador — candidato S2

Implementação de ensaio do ADR-0036, sem instalação nem ativação. O Worker
continua com a composição anterior até S3. O perfil `s2-candidate-v1` e as
imagens Debug sintéticas não constituem release ou homologação de produção.

## Autoridade e identidade

O launcher Linux executa como usuário não root exclusivo, proprietário de um
daemon Docker rootless exclusivo com cgroup v2/systemd. O Worker usa outro UID
e só acessa o socket de controle; não recebe acesso ao daemon. O launcher
confere `SO_PEERCRED` antes de ler a requisição. O grupo do socket permite a
conexão, mas não substitui a conferência do UID.

O arquivo de perfil é regular, absoluto, sem symlink e não gravável por grupo
ou terceiros; pertence ao administrador ou ao launcher. Contém apenas schema 1,
digest imutável da imagem previamente carregada, socket rootless do próprio UID,
socket de controle, diretório de estado, UID do Worker e GID do controle. Campos
extras são recusados. Nenhuma requisição escolhe imagem, comando, argumentos,
ambiente, mount, path ou recursos adicionais.

A imagem não declara volumes. O daemon dedicado recusa objetos de outros
aplicativos e só recupera containers com nome/id e label gerados pelo launcher.
Não existe fallback para `/var/run/docker.sock`, daemon compartilhado ou execução
privilegiada. O CLI usa configuração vazia e ambiente mínimo, sem credenciais,
contextos ou variáveis herdadas do serviço.

## Framing v1

Todos os inteiros do protocolo S1 são little-endian. O id opaco tem 16 bytes;
não contém identificação de participante. Uma conexão transporta uma operação
e o cliente encerra o lado de escrita antes de receber a resposta.

| Requisição | Conteúdo |
|---|---|
| Cabeçalho, 22 bytes | `DMLC`, versão `01`, operação de 1 byte, id de 16 bytes |
| Iniciar (`01`) | Uma requisição DMRQ v1 completa, com nonce igual ao id, seguida de EOF |
| Consultar (`02`) | Apenas o cabeçalho, seguido de EOF |
| Cancelar (`03`) | Apenas o cabeçalho, seguido de EOF |

Uma resposta tem cabeçalho de 54 bytes: `DMLR`, versão `01`, resultado de 1 byte,
id de 16 bytes e SHA-256 do perfil de 32 bytes. Somente resultado `00` pode conter
o stream de resposta S1 após esse cabeçalho; todos os demais têm payload vazio.
Entrada inválida, UID indevido, framing extra ou timeout podem encerrar a conexão
sem resposta. Não existe mensagem de erro que reflita bytes ou paths recebidos.

| Resultado | Significado |
|---|---|
| `00` | Processo saiu com código zero, sem OOM, e limpeza confirmada dentro do prazo |
| `01` | Único slot ocupado |
| `02` | Id já admitido; nenhum resultado anterior é retransmitido |
| `03` | Tentativa cancelada |
| `04` | Tentativa falhou |
| `05` | Id desconhecido |
| `06` | Tentativa em execução |
| `07` | Id terminou, sem retransmissão de dados |
| `08` | Launcher bloqueado por confirmação de limpeza/estado indisponível |

`00` confirma a fronteira de processo; não concede aprovação editorial nem
dispensa a validação integral do parser S1 pelo consumidor de S3. Consulta não
retorna bytes e cancelamento não concede um segundo slot antes da limpeza.

## Limites e falhas

O perfil candidato fecha rede, usa UID 1654, rootfs somente leitura, capabilities
vazias, no-new-privileges e seccomp builtin do runtime revisado. Impõe 512 MiB,
swap zero, uma CPU, 64 PIDs e tmpfs de 16 MiB com noexec/nosuid/nodev. Não monta
storage da aplicação, segredos, banco ou socket Docker dentro do sandbox.

O launcher limita entrada a 5 MiB, variante a 16 MiB, saída S1 a 24 MiB mais
4 KiB de framing e stderr a 64 KiB descartados. O pedido só pode restringir
esses orçamentos e o prazo máximo de 50 s. O prazo monotônico externo inclui
transferência/execução e reserva de limpeza; timeout, excesso de stream, OOM,
SIGKILL ou crash após uma resposta aparente nunca liberam os bytes acumulados.
Não altera o algoritmo nem o alocador ImageSharp existente de 256 MB.

O journal é privado, atômico e limitado a 4096 admissões/2 MiB. Só contém id,
estado, prazo e digest; não armazena fotos, stdout/stderr ou detalhes de erro.
Não expurga ids automaticamente: lotação bloqueia novas admissões e exige
procedimento operacional próprio antes de uso contínuo. Reinício recupera
órfãos antes de abrir o controle; confirmação impossível mantém o launcher
bloqueado. A unidade systemd é apenas um template, sem instalação automática.

## Evidência e trabalho restante

`scripts/deploy/test_media_launcher.py` verifica framing, identidade local,
deduplicação, limites e supervisão. `scripts/media/test_sanitizer_isolation.py`
executa exclusivamente em runners descartáveis do Actions e mede enforcement
real nas duas arquiteturas, falhas nativas e o executável sanitizador. O probe
de falhas é uma imagem separada; não entra na imagem do sanitizador.

S3 ainda precisa integrar o transporte no Worker, validar S1 antes do commit e
retirar seu decoder. A revisão de segurança, release licenciada, orçamento
completo de launcher/daemon/buffers e homologação do host continuam gates de
#174/#86 antes de qualquer instalação ou dados reais. Upload e filtro regional
permanecem inativos.
