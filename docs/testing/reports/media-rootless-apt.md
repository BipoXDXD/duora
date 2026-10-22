# Regressão das fontes APT do runtime rootless — 22/10/2026

A execução [39866166531](https://github.com/BipoXDXD/duora/actions/runs/39866166531),
main `a27e8f8`, falhou em AMD64 antes da matriz: o update geral recebeu HTTP 403
no InRelease do repositório Microsoft para Ubuntu noble (job `120128776339`).
ARM64 passou; isso não dispensava a prova AMD64. O erro não demonstrou falha do decoder.

## Fontes necessárias e herdadas

| Fonte | Uso nesta etapa |
|---|---|
| Ubuntu noble, updates e security, conforme `ubuntu.sources` do runner | `uidmap`, `dbus-user-session`, `slirp4netns` e dependências do sistema |
| Docker stable/noble, arquitetura de `dpkg` | `docker-ce-rootless-extras` |
| Microsoft e demais fontes adicionais do runner | Não fornecem esses pré-requisitos; ficam fora da seleção |

O [script da imagem AMD64 observada](https://github.com/actions/runner-images/blob/ubuntu24/20261012.334/images/ubuntu/scripts/build/install-ms-repos.sh)
configura Microsoft previamente. Acrescentar Docker não restringe a leitura das outras fontes.

O [PR #225](https://github.com/BipoXDXD/duora/pull/225), integrado em
`ba5f41cad78015ab1cccd3f156feb12892fcf4d6`, já corrigiu os workflows de isolamento e
publicação candidata. Cada etapa usa um diretório temporário com apenas a cópia
integral de `ubuntu.sources` e uma entrada Docker nativa; `sourcelist=/dev/null`
exclui a lista principal, `sourceparts` seleciona o diretório próprio e `lists`
seleciona índices novos. Update e install usam os mesmos argumentos.
`APT::Update::Error-Mode=any` torna erros transitórios igualmente fatais.
A configuração global não é apagada nem desabilitada.

Preservam-se o Signed-By Ubuntu e Docker, o fingerprint Docker
`9DC858229FC7DD38854AE2D88D81803C0EBFCD88`, os digests/versões já fixados,
a arquitetura nativa, cgroups v2/systemd, seccomp e a matriz completa.
A correção não introduz pin de versão dos pacotes APT: a disponibilidade dos
repositórios necessários e a evolução da imagem hospedada continuam dependências.
Se `ubuntu.sources` deixar de existir, a etapa falha; não há fallback irrestrito.

Referências: [configuração APT](https://manpages.debian.org/bookworm/apt/apt.conf.5.en.html),
[update/install e simulação](https://manpages.debian.org/bookworm/apt/apt-get.8.en.html),
[pré-requisitos rootless Docker](https://docs.docker.com/engine/security/rootless/).

## Regressão adicionada

`python3 scripts/media/test_rootless_apt.py -v` é obrigatório nos dois runners
nativos, antes de instalar os pré-requisitos. Usa APT e GPG reais, chave efêmera,
metadados assinados sintéticos e HTTP local; a instalação é simulada, com estado
privado, sem alterar pacotes do host. As opções e os comandos são extraídos dos
dois workflows; somente os pacotes são substituídos por um pacote sintético.

- Controle negativo: update irrestrito consulta fonte herdada HTTP 403 e retorna
  100 antes do install, reproduzindo a classe de falha original.
- Update e resolução de instalação restritos passam com fonte assinada, sem
  consultar fontes herdadas na lista principal ou no diretório adicional.
- Fonte necessária HTTP 403 ou HTTP 503 retorna 100 e impede install.
- InRelease adulterado retorna 100/BADSIG e impede install.

A fixture não substitui a instalação real nem a matriz de isolamento, que
continuam obrigatórias depois dela nas duas arquiteturas. Não testa disponibilidade
futura da internet nem transforma CI em homologação operacional.

## Evidência anterior à regressão e limites operacionais

Após #225, [isolamento main 39870546067](https://github.com/BipoXDXD/duora/actions/runs/39870546067)
e [CI main 39870545978](https://github.com/BipoXDXD/duora/actions/runs/39870545978)
concluíram com sucesso. O PR desta regressão deve registrar separadamente as
execuções de seu próprio SHA final antes da integração.

Escopo: suporte a INV-12/T-16; sem alteração de API-M01–M04 ou dos efeitos
INV-05/06. Nenhum build, Docker ou teste de runtime no Mac. Sem cancelamentos,
reexecuções administrativas, dispatch de publicação, merge, SSH, ativação ou dados
reais. A janela operacional permanece coordenada pelo responsável. #174 continua
aberta: pacote licenciado verificável, homologação do host e capacidade possuem
gates próprios. Reversão desta entrega remove somente a regressão e seu passo;
não reintroduz APT irrestrito nem fallback de decode no Worker.
