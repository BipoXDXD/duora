# Imagens .NET da release: web, worker e migrator (um alvo cada), a partir do mesmo build.
#   docker buildx build -f infra/images/dotnet/Dockerfile --target web --platform linux/arm64 .
# Contexto = raiz do repositório (.dockerignore da raiz). Bases fixadas por tag + digest do índice multiarquitetura;
# atualizar as duas juntas. Detalhes e decisões: infra/README.md.

# --- Build: roda na arquitetura da máquina de build (sem emulação) ---------------------------------------------
# A saída de web e worker é IL portátil (UseAppHost=false), igual para amd64 e arm64; só o bundle de migração tem
# código nativo e por isso é gerado com o RID do alvo.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_NOLOGO=true

COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY .config/dotnet-tools.json .config/
COPY backend/src/ backend/src/

# --locked-mode: falha se algum packages.lock.json divergir, como no CI.
# Pacotes e obj pertencem à mesma camada: cache mounts são descartáveis e podem desaparecer
# antes do publish --no-restore (NETSDK1064). Só /out é copiado para as imagens finais.
RUN dotnet restore backend/src/Duora.Web/Duora.Web.csproj --locked-mode \
 && dotnet restore backend/src/Duora.Worker/Duora.Worker.csproj --locked-mode

FROM build AS publish-web
# /dataprotection-keys: ponto de montagem do volume de chaves; o volume nomeado herda dono e modo na 1a montagem.
RUN dotnet publish backend/src/Duora.Web/Duora.Web.csproj -c Release --no-restore -o /out \
      -p:UseAppHost=false -p:ContinuousIntegrationBuild=true \
 && mkdir -p /dataprotection-keys

FROM build AS publish-worker
# O ImageSharp 4 (ADR-0025) só compila em Release com a licença da Six Labors. Ela chega como secret do BuildKit
# (docker build --secret id=sixlabors_license,env=SIXLABORS_LICENSE_KEY), montado só durante este RUN em
# /run/secrets: não vira ARG, ENV nem arquivo de camada, e o estágio final copia só /out. required=true falha o build
# já aqui, com o nome do secret, em vez de compilar sem licença. Custódia e rotação: docs/operations/deploy.md.
RUN --mount=type=secret,id=sixlabors_license,required=true \
    dotnet publish backend/src/Duora.Worker/Duora.Worker.csproj -c Release --no-restore -o /out \
      -p:UseAppHost=false -p:ContinuousIntegrationBuild=true \
      -p:SixLaborsLicenseFile=/run/secrets/sixlabors_license

FROM build AS bundle
ARG TARGETARCH
RUN case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "Arquitetura sem suporte: $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && dotnet tool restore \
 # Restore com RID: o bundle é self-contained. As versões continuam as do lockfile validado no estágio build
 # (pinagem central + transitiva); só entram os pacotes de runtime do alvo.
 && dotnet restore backend/src/Duora.Infrastructure/Duora.Infrastructure.csproj --runtime "$rid" \
 && dotnet ef migrations bundle \
      --project backend/src/Duora.Infrastructure \
      --startup-project backend/src/Duora.Infrastructure \
      --configuration Release \
      --runtime "$rid" --self-contained \
      --output /out/efbundle

# --- web: ASP.NET Core em Ubuntu chiseled (sem shell, sem gerenciador de pacotes, usuário app 1654) ----------------
# A variante -extra traz ICU e tzdata: o app não roda em modo de globalização invariante.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble-chiseled-extra@sha256:9b755ea799ae649141a073e30d8b62d8095a5465af2b7694d17866e8c05131d8 AS web
ARG GIT_COMMIT=unknown
LABEL org.opencontainers.image.title="duora-web" \
      org.opencontainers.image.source="https://github.com/BipoXDXD/duora" \
      org.opencontainers.image.revision="${GIT_COMMIT}"
WORKDIR /app
# Binários do root e só leitura para o usuário da aplicação.
COPY --from=publish-web /out/ ./
# O key ring fica em DataProtection:KeysDirectory (obrigatório; o Compose aponta para este diretório e monta o
# volume aqui). Proteção das chaves em repouso é pendência do backend (spec 11.2, infra/README.md).
COPY --from=publish-web --chown=1654:1654 --chmod=700 /dataprotection-keys /home/app/.aspnet/DataProtection-Keys
USER 1654:1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "Duora.Web.dll"]

# --- worker: runtime .NET puro (sem ASP.NET Core), mesmo hardening -----------------------------------------------
# O runtimeconfig do worker não pode exigir Microsoft.AspNetCore.App (ADR-0019, "Revisão: separação do runtime
# web"); Duora.ArchitectureTests falha no build se isso mudar, antes de a imagem quebrar no start.
FROM mcr.microsoft.com/dotnet/runtime:10.0.14-noble-chiseled-extra@sha256:2fbc014e87df3d50948f8710f200ddca0183388904ec35c8ad7e481ef6f8a230 AS worker
ARG GIT_COMMIT=unknown
LABEL org.opencontainers.image.title="duora-worker" \
      org.opencontainers.image.source="https://github.com/BipoXDXD/duora" \
      org.opencontainers.image.revision="${GIT_COMMIT}"
WORKDIR /app
COPY --from=publish-worker /out/ ./
USER 1654:1654
ENTRYPOINT ["dotnet", "Duora.Worker.dll"]

# --- migrator: bundle do EF Core, executado por job exclusivo (nunca no startup do web/worker) --------------------
# runtime-deps não chiseled: o bundle é self-contained e o wrapper em sh lê a connection string do arquivo de segredo.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.14-noble@sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832 AS migrator
# apt/libapt-pkg6.0t64 corrigidos: o digest acima traz 2.8.3, com CVE-2026-109518 (HIGH, corrigível). Sem versão exata
# porque o Ubuntu só mantém a mais nova no arquivo, e fixar quebraria o build seguinte; sai quando a base trouxer a
# correção.
RUN apt-get update \
 && apt-get install -y --only-upgrade --no-install-recommends apt libapt-pkg6.0t64 \
 && rm -rf /var/lib/apt/lists/*
ARG GIT_COMMIT=unknown
LABEL org.opencontainers.image.title="duora-migrator" \
      org.opencontainers.image.source="https://github.com/BipoXDXD/duora" \
      org.opencontainers.image.revision="${GIT_COMMIT}"
WORKDIR /app
COPY --from=bundle /out/efbundle ./efbundle
COPY --chmod=755 infra/images/dotnet/migrate.sh ./migrate.sh
COPY --chmod=644 infra/images/dotnet/reconcile-privileges.sql ./reconcile-privileges.sql
RUN umask 022 && sha256sum reconcile-privileges.sql > reconcile-privileges.sql.sha256
USER 1654:1654
ENTRYPOINT ["/app/migrate.sh"]
