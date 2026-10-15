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

# --- Caddy: usuário sem privilégio; as portas 80/443 vêm do sysctl do Compose, não de capability ------------------
FROM caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f AS edge
ARG GIT_COMMIT=unknown
LABEL org.opencontainers.image.title="duora-edge" \
      org.opencontainers.image.source="https://github.com/BipoXDXD/duora" \
      org.opencontainers.image.revision="${GIT_COMMIT}"

# /data guarda certificados e chaves do ACME e /config o autosave; os volumes nomeados herdam dono e modo 0700.
# A imagem oficial dá cap_net_bind_service ao binário por capability de arquivo; com cap_drop ALL o exec falha
# ("operation not permitted"). A cópia sem xattr remove a capability: as portas baixas vêm do sysctl do Compose.
RUN cp /usr/bin/caddy /usr/bin/caddy.nocap \
 && mv /usr/bin/caddy.nocap /usr/bin/caddy \
 && addgroup -S -g 10001 caddy \
 && adduser -S -D -H -u 10001 -G caddy -s /sbin/nologin caddy \
 && chown -R caddy:caddy /data /config \
 && chmod 0700 /data /config

COPY infra/caddy/Caddyfile /etc/caddy/Caddyfile
COPY --from=spa /src/frontend/dist/ /srv/

USER 10001:10001
EXPOSE 80 443 443/udp
ENTRYPOINT ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
