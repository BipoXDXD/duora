# Borda do Duora: Caddy com o build estático da SPA e o Caddyfile embutidos (a imagem é a release do frontend).
#   docker buildx build -f infra/images/edge/Dockerfile --platform linux/arm64 .
# Contexto = raiz do repositório. Detalhes: infra/README.md.

# --- SPA: Node só no build; a saída é estática e igual para qualquer arquitetura -----------------------------------
FROM --platform=$BUILDPLATFORM node:24.22.0-alpine@sha256:44fce696f38ba8c987c4db89954051000bbd05ffd8d8659ba15ed4a8602e937f AS spa
WORKDIR /src/frontend
# pnpm na versão do packageManager do package.json, via Corepack (como no CI).
ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0
RUN corepack enable
COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN --mount=type=cache,id=duora-pnpm,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile
COPY frontend/ ./
RUN pnpm build

# --- Caddy recompilado: mesma versão da imagem oficial, com Go e x/net corrigidos --------------------------------
# O binário da imagem oficial 2.11.7 foi compilado com Go 1.26.8 e golang.org/x/net 0.59.0, com CVEs HIGH de DoS em
# HTTP/2 e TLS (CVE-2026-78667, CVE-2026-78669, CVE-2026-97031), justamente na borda pública. Recompilar com os
# patches resolve sem exceção no .trivyignore. Quando a imagem oficial trouxer o Go corrigido, volte a usar o
# binário dela e remova este estágio. Compilação cruzada (CGO desligado), sem emulação.
FROM --platform=$BUILDPLATFORM golang:1.26.9-alpine@sha256:3082400e369fa24d5fc60bca20edab3f6d604e0c5a690ec66b295eff4dd87ade AS caddy-build
ARG TARGETOS
ARG TARGETARCH
ENV CGO_ENABLED=0
RUN go install github.com/caddyserver/xcaddy/cmd/xcaddy@v0.4.7
RUN GOOS="$TARGETOS" GOARCH="$TARGETARCH" XCADDY_GO_BUILD_FLAGS="-trimpath -ldflags=-s" \
    xcaddy build v2.11.7 --replace golang.org/x/net=golang.org/x/net@v0.60.0 --output /out/caddy

# --- Caddy: usuário sem privilégio; as portas 80/443 vêm do sysctl do Compose, não de capability ------------------
FROM caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f AS edge
ARG GIT_COMMIT=unknown
LABEL org.opencontainers.image.title="duora-edge" \
      org.opencontainers.image.source="https://github.com/BipoXDXD/duora" \
      org.opencontainers.image.revision="${GIT_COMMIT}"

# /data guarda certificados e chaves do ACME e /config o autosave; os volumes nomeados herdam dono e modo 0700.
RUN addgroup -S -g 10001 caddy \
 && adduser -S -D -H -u 10001 -G caddy -s /sbin/nologin caddy \
 && chown -R caddy:caddy /data /config \
 && chmod 0700 /data /config

# Substitui o binário oficial. O arquivo novo não carrega a capability de arquivo (cap_net_bind_service) da imagem
# oficial, que com cap_drop ALL faria o exec falhar ("operation not permitted").
COPY --from=caddy-build /out/caddy /usr/bin/caddy
COPY infra/caddy/Caddyfile /etc/caddy/Caddyfile
COPY --from=spa /src/frontend/dist/ /srv/

USER 10001:10001
EXPOSE 80 443 443/udp
ENTRYPOINT ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
