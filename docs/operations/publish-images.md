# Publicação de imagens

O workflow `Publish release images` publica as cinco imagens no GHCR, sem acesso à VPS, ao Coolify ou a
credenciais de produção. Atende à separação de credenciais e à rastreabilidade da spec 17.1–17.2 e INV-12;
não altera APIs, estado de participantes nem o fluxo de deploy. O CI existente permanece obrigatório.

Depois de revisar e integrar o workflow na `main`, execute pela branch `main`, informando o SHA completo
em `commit`. O commit precisa pertencer ao histórico da `main`; seu último run do workflow `CI` para
`push` na `main`, na tentativa atual, deve estar concluído com os nove jobs aprovados. Check ausente,
duplicado, pulado, cancelado ou falho impede a publicação. O teste do verificador também roda no PR.

Cada runner nativo (amd64 e arm64) constrói as cinco imagens dos Dockerfiles desse commit, confere o segredo
da licença, executa os binários, os testes de privilégios e o drill de restore, e varre HIGH/CRITICAL
corrigíveis com Trivy. Só então gera SBOMs CycloneDX e publica **essas mesmas imagens locais**, sem novo build.
A licença Six Labors permanece no secret do Actions e no mount temporário do BuildKit; nenhuma chave de
registro persistente é necessária: o job usa `GITHUB_TOKEN` com `packages: write` e encerra o login ao terminar.

Os nomes são `ghcr.io/<owner>/duora-{web,worker,migrator,edge,postgres}`. As tags incluem commit, run, tentativa
e arquitetura; não há `latest` nem substituição automática de tags de outra execução. O acesso aos pacotes
segue a configuração do GHCR; para pull privado no host use credencial somente de leitura configurada pelo
operador. O workflow não torna pacotes públicos.

Somente um **workflow inteiro concluído com sucesso** qualifica uma publicação. Falha pode deixar imagens
parciais no registry; elas não autorizam deploy. Reexecute todos os jobs, não apenas os jobs falhos, para
obter evidência e artefatos das duas arquiteturas na mesma tentativa.

Baixe `release-amd64-<tentativa>` ou `release-arm64-<tentativa>`. Cada artefato contém:

- `publication.json`: commit, arquitetura, links do CI e da publicação, cinco referências `repo@sha256:...`;
- `images.json`: o mapa de referências por digest;
- `sbom-<imagem>.cdx.json`: SBOM de cada imagem publicada.

Escolha a arquitetura do host. As imagens são publicadas por arquitetura; não há índice multi-arquitetura.
Use os digests, nunca as tags, na configuração de execução. Guarde os artefatos com a evidência da release
antes dos 90 dias de retenção. `publication.json` é evidência de publicação, **não** é o manifesto aceito
pelo `duora-deploy`: complete o manifesto de [deploy.md](deploy.md) com migrations, compatibilidade de schema,
referências de SBOM e rollback revisados. Backup e smoke continuam necessários antes de liberar produção.

Rollback usa os digests da release anterior elegível, preservando o banco conforme o runbook; publicar uma
imagem não troca serviços. Desabilitar este workflow interrompe novas publicações sem afetar imagens existentes.

Validação local do fluxo, sem segredos nem publicação:

```sh
python3 -m unittest discover -s scripts/release -v
actionlint .github/workflows/publish-images.yml
git diff --check
```
