# Base publicada de leitores — #173 / complemento de M1

## Objetivo e limite

O relatório M1 de #184 não havia exercitado um binário histórico publicado.
Este ensaio executa a imagem Web imutável abaixo, sem recompilar ou substituir
suas assemblies, contra PostgreSQL 18 descartável com o schema expandido pelas
migrations atuais. WebApplicationFactory prepara dados sintéticos e é encerrada
antes de iniciar o binário publicado. As requisições de verificação passam por
HTTPS, cookies, CSRF, login e endpoints reais desse container.

É prova da expansão M1 presente nesta revisão, não homologação de migrations
futuras de M2–M7, do Worker, da SPA histórica ou de infraestrutura operacional.
Não habilita escritores HumanReview, upload real, serviços ou filtro regional.
#173, #174, #86 e #69 continuam abertas até seus próprios critérios completos.

## Referência imutável

- Commit publicado: `2ab2593d5d1312f56e71cd091bfe0167b1bcd685`, descendente de
  #184 / `4b5d0a14abad7176f5cb476b423d097fa778216c`.
- Origem: [publicação 39756720942, tentativa 1](https://github.com/BipoXDXD/duora/actions/runs/39756720942/attempts/1),
  artefato `release-multiarch-1/publication.json`.
- Web: `ghcr.io/bipoxdxd/duora-web@sha256:92651e1fc7c78b4686d9e85adc2bbcd5bae83951b7b577724f665a84bd1dbad5`.
- Índice amd64/arm64; a execução de `backend` deste ensaio é amd64 nativa.
  Não inferir uma prova arm64 de rollback a partir desse índice.

A referência está fixada no helper `PublishedReviewReader`; não usa `latest`,
tag mutável, pull com credencial de produção ou rebuild da aplicação.

## Casos e contenção

`MediaReviewPublishedRollbackTests` usa banco e rede próprios, os papéis reais
de teste `duora_app`/`duora_migrator`, migrations atuais e dados sintéticos.
O container antigo se conecta como aplicação e não aplica migrations no startup.
Um certificado efêmero é aceito somente pelo hash exato nesse cliente de teste;
nenhuma validação TLS do produto é alterada. Os testes mantêm cookies separados
por navegador e os headers CSRF/origem usuais.

| Prova | Resultado exigido |
|---|---|
| AwaitingReview e três códigos editoriais de rejeição | Estado/código preservados no status, com no-store |
| Repetição de mesmo corpo/chave | Mesmo id/estado; corpo diferente permanece conflito |
| Titular diferente | 404 sem revelar a foto |
| Três esperas antigas por conta | UPLOAD_PENDING_LIMIT, sem gravação nova |
| Container antigo recriado sobre o mesmo banco e teto global restrito | MEDIA_QUEUE_FULL contando AwaitingReview |
| Integridade final | Sete uploads originais, nenhum asset aprovado ou upload duplicado |

As esperas são criadas por SQL somente no fixture sintético, como no ensaio M1;
não se introduz escritor HumanReview no código da aplicação. Nenhum decoder,
política, schema de produção, montagem ou conteúdo real é modificado.

## Execução e sequência

Verificação somente no GitHub Actions: os nove gates existentes, incluindo
`dotnet test --solution backend/Duora.slnx --no-build`. Não há Docker, build,
restore ou teste pesado no Mac. O resultado desta revisão deve ser registrado
no PR com SHA e execução finais; este documento define a prova, sem antecipar
sucesso.

Depois de aprovado, o digest é uma base candidata de reversão para o schema
atual. Cada expansão seguinte precisa repetir a prova antes dos escritores;
compatibilidade do Worker/SPA e homologação operacional continuam necessárias.
Falha exige parada segura ou correção para frente, nunca apagar/reclassificar
pendências para permitir um binário incompatível.

Referências de método: [aplicação real fora de WebApplicationFactory](https://dometrain.com/take/course/from-zero-to-hero-integration-testing-in-asp-net-core-2724127/how-our-docker-strategy-changes-53953967/)
e [rede/portas aleatórias do Testcontainers](https://dotnet.testcontainers.org/api/create_docker_network/).
