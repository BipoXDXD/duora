# Imagens .NET da release: web, worker e migrator (um alvo cada), a partir do mesmo build.
#   docker buildx build -f infra/images/dotnet/Dockerfile --target web --platform linux/arm64 .
# Contexto = raiz do repositório (.dockerignore da raiz). Bases fixadas por tag + digest do índice multiarquitetura;
# atualizar as duas juntas. Detalhes e decisões: infra/README.md.

# --- Build: roda na arquitetura da máquina de build (sem emulação) ---------------------------------------------
# A saída de web e worker é IL portátil (UseAppHost=false), igual para amd64 e arm64; só o bundle de migração tem
# código nativo e por isso é gerado com o RID do alvo.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402-noble@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_NOLOGO=true

COPY global.json nuget.config Directory.Build.props Directory.Packages.props .editorconfig ./
COPY .config/dotnet-tools.json .config/
COPY backend/src/ backend/src/

# --locked-mode: falha se algum packages.lock.json divergir, como no CI.
RUN --mount=type=cache,id=duora-nuget,target=/root/.nuget/packages \
    dotnet restore backend/src/Duora.Web/Duora.Web.csproj --locked-mode \
 && dotnet restore backend/src/Duora.Worker/Duora.Worker.csproj --locked-mode

FROM build AS publish-web
# /dataprotection-keys: ponto de montagem do volume de chaves; o volume nomeado herda dono e modo na 1a montagem.
RUN --mount=type=cache,id=duora-nuget,target=/root/.nuget/packages \
    dotnet publish backend/src/Duora.Web/Duora.Web.csproj -c Release --no-restore -o /out \
      -p:UseAppHost=false -p:ContinuousIntegrationBuild=true \
 && mkdir -p /dataprotection-keys

FROM build AS publish-worker
RUN --mount=type=cache,id=duora-nuget,target=/root/.nuget/packages \
    dotnet publish backend/src/Duora.Worker/Duora.Worker.csproj -c Release --no-restore -o /out \
      -p:UseAppHost=false -p:ContinuousIntegrationBuild=true

FROM build AS bundle
ARG TARGETARCH
RUN --mount=type=cache,id=duora-nuget,target=/root/.nuget/packages \
    case "$TARGETARCH" in \
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374 AS web
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

# --- worker: mesma base do web, mesmo hardening --------------------------------------------------------------------
# O Duora.Infrastructure referencia o framework do ASP.NET Core (Identity, Data Protection; ADR-0019), e o
# runtimeconfig do worker exige Microsoft.AspNetCore.App: na imagem runtime pura o processo nem inicia.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra@sha256:3ed6558056b54d5c9dfefb7ff473728e022ff2bf1d976cbf8ee07b05ec594374 AS worker
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
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.13-noble@sha256:da5846024d0008b48259b53a8953d39381fe5b7ec99ef1449ba4ba927f201490 AS migrator
ARG GIT_COMMIT=unknown
LABEL org.opencontainers.image.title="duora-migrator" \
      org.opencontainers.image.source="https://github.com/BipoXDXD/duora" \
      org.opencontainers.image.revision="${GIT_COMMIT}"
WORKDIR /app
COPY --from=bundle /out/efbundle ./efbundle
COPY --chmod=755 infra/images/dotnet/migrate.sh ./migrate.sh
USER 1654:1654
ENTRYPOINT ["/app/migrate.sh"]
