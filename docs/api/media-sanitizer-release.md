# Pacote do sanitizador — contrato candidato S4

`scripts/media/release_contract.py` confere um pacote local contra um SHA-256
obtido separadamente de uma publicação confiável. Não baixa, extrai, executa,
instala ou ativa componentes. O contrato complementa o
[controle S2/S3](media-launcher-control.md) e o ADR-0036; não altera o manifesto
Coolify das cinco imagens da aplicação.

## Conteúdo e identidade

O diretório contém exatamente oito arquivos regulares, sem symlinks ou hardlinks:

| Arquivo | Limite |
|---|---|
| `manifest.json` | 16 KiB |
| `profile.json` | 4096 bytes |
| `isolation.json` | 64 KiB |
| `sbom.cdx.json` | 8 MiB |
| `launcher.py`, `docker_runtime.py`, `run_launcher.py`, `duora-media-launcher.service` | 256 KiB por arquivo |

`verify_bundle(directory, expected_manifest_sha256, expected_commit,
expected_architecture, expected_scope)` devolve o manifesto validado ou lança
`ValueError`. Os argumentos esperados vêm do operador/fluxo confiável, nunca
do próprio pacote. A leitura limita os bytes antes de interpretar JSON e usa
descritores com `O_NOFOLLOW`; duplicatas JSON e constantes não finitas são recusadas.

O manifesto v1 tem campos fechados: `schema` igual a 1; `scope` igual a
`synthetic-candidate` ou `host-candidate`; `commit` de 40 hexadecimais minúsculos;
`architecture` igual a `amd64` ou `arm64`; `image` no repositório
`ghcr.io/bipoxdxd/duora-media-sanitizer` fixada por `@sha256:`; `imageConfig`
fixado por `sha256:`; `launcherVersion` igual a `s2-control-v1`;
`protocol` exatamente `{control: 1, stream: 1}`; `launcherUid`; `profileSha256`;
`files` com os sete hashes dos arquivos restantes; `ci`, `isolation` e
`publication`, cada um contendo somente `run` e `attempt` inteiros positivos.
Hashes têm 64 hexadecimais minúsculos. Referências de execução cabem em inteiro
positivo de 63 bits; UID/GID em inteiro positivo de 31 bits. Booleanos não são inteiros.

A imagem do perfil deve coincidir com a do manifesto. `profileSha256` é o
digest canônico de `Profile.from_dict`/`Profile.digest`, incluindo a política
`s2-candidate-v1`, UID/GID, sockets, diretório e imagem. Não é o hash bruto do
arquivo JSON; ambos os vínculos são conferidos. O Worker usa esse mesmo pin
no controle S3. Um perfil do runner não é um perfil universal de produção.

## Prova vinculada

`isolation.json` precisa indicar candidato completo, a arquitetura nativa
(`x86_64`/`aarch64`), imagem/configuração e pin do perfil exatamente iguais aos
do manifesto. Os 14 casos S2 devem aparecer uma única vez, todos aprovados,
com duração finita não negativa. Também são obrigatórios os controles efetivos,
Docker rootless com cgroup v2/systemd, OOM observado, controle negativo de rootfs
gravável e resposta sintética limitada com hash. O perfil candidato continua
512 MiB, swap zero, uma CPU, 64 PIDs e demais restrições S2.

O SBOM precisa ter envelope CycloneDX 1.5 ou 1.6, versão positiva e lista de
componentes. Esta validação de envelope não substitui a geração de SBOM nem os
scanners de licença/vulnerabilidade do produtor confiável.

Hashes internos não autenticam o produtor. Reescrever payload e manifesto não
supera o pin externo original. Referências de execução no JSON também não
comprovam sucesso por si: a publicação deve conferir workflow, commit,
execução/tentativa, gates e as duas provas nativas antes de distribuir o pin.
A verificação é uma fotografia limitada dos bytes lidos; não autoriza executar
posteriormente arquivos de um diretório que continue gravável por terceiros.

## Entregas restantes

Este contrato não publica imagem Release licenciada. A publicação protegida
separada deve gerar uma imagem única, conferir ausência de licença nas camadas,
executar scanners, produzir SBOM e os 14 ensaios contra seu digest imutável nas
duas arquiteturas. Os novos campos `imageReference` e `profileSha256` precisam
ser capturados do perfil efetivamente ensaiado; não podem ser acrescentados por
suposição a um relatório S2 anterior.

Nenhum scope representa aprovação de produção. Instalação, homologação do host,
orçamento conjunto com buffers/daemon/storage/backup (#86), limpeza durável de
originais e reversão operacional continuam requisitos antes do fechamento da
#174. Upload, serviços e filtro regional permanecem inativos.
