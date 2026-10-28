"""Apoio comum aos testes da borda: Caddy real (test_caddy_*.py) e smoke local (smoke_local.py).

A política esperada da CSP e da Permissions-Policy mora aqui, num só lugar, para os dois conferirem o mesmo valor.
O porquê de cada diretiva e de cada feature está em docs/operations/deploy.md ("Cabeçalhos de segurança da borda").
"""

from __future__ import annotations

import shutil
import subprocess
from collections.abc import Callable, Iterable
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
CADDYFILE = REPO / "infra" / "caddy" / "Caddyfile"
# Mesma imagem do estágio "edge" do infra/images/edge/Dockerfile (a edge só troca o binário por um build do xcaddy).
IMAGE = "caddy:2.11.7-alpine@sha256:d8542f48d34a9cf4e4c11a478865229840e87e4c96ea3f439101f31a5d35f75f"
READY_TIMEOUT_SECONDS = 30

# Hash do único <style> que o <model-viewer> 4.4.0 põe no shadow root (lib/template.js, igual nos bundles de dist/).
# Muda com a versão do pacote: subir o <model-viewer> exige recalcular o hash e trocar aqui e no Caddyfile.
MODEL_VIEWER_STYLE_HASH = "'sha256-PXtZZkJMMyc18o/DN1ZEFOJ2qEGq95IQbVaJFMOXOb0='"

# Diretiva -> fontes, com DUORA_DOMAIN=localhost na 443 (Caddy de teste e smoke).
EXPECTED_CSP = {
    "default-src": ["'none'"],
    "script-src": ["'self'"],
    "style-src": ["'self'", MODEL_VIEWER_STYLE_HASH],
    "img-src": ["'self'"],
    "font-src": ["'self'"],
    "media-src": ["'self'"],
    # blob: só aqui: o GLTFLoader lê a textura embutida no GLB por fetch(blob:) (ImageBitmapLoader).
    "connect-src": ["'self'", "blob:", "wss://localhost"],
    "manifest-src": ["'self'"],
    "worker-src": ["'none'"],
    "base-uri": ["'none'"],
    "form-action": ["'self'"],
    "frame-ancestors": ["'none'"],
    "object-src": ["'none'"],
    "require-trusted-types-for": ["'script'"],
    "trusted-types": ["lit-html"],
}

# Única página com a política da selfie (issue #93): o documento /account/selfie, carregado por navegação completa
# (a SPA recarrega ao entrar e ao sair). Acrescenta só o que o FaceLivenessDetector 3.7.0 oficial da AWS usa:
# 'wasm-unsafe-eval' para compilar o WASM do TensorFlow.js, o CDN da AWS que serve esse WASM e o modelo BlazeFace
# (defaults do componente) e o WebSocket do StartFaceLivenessSession em us-east-1. Nada de worker, blob: novo ou
# curinga. Fonte: https://ui.docs.amplify.aws/react/connected-components/liveness (binaryPath, faceModelUrl).
SELFIE_PATH = "/account/selfie"
LIVENESS_CDN = "https://cdn.liveness.rekognition.amazonaws.com"
LIVENESS_STREAMING = "wss://streaming-rekognition.us-east-1.amazonaws.com"
EXPECTED_SELFIE_CSP = EXPECTED_CSP | {
    "script-src": ["'self'", "'wasm-unsafe-eval'"],
    "connect-src": [*EXPECTED_CSP["connect-src"], LIVENESS_CDN, LIVENESS_STREAMING],
}

# Allowlist: só o que a voz ao vivo e o áudio usam, e só para a própria origem.
ALLOWED_FEATURES = ("autoplay", "microphone", "speaker-selection")

# Toda outra feature que o Chromium reconhece numa página comum (permissions_policy_features.json5, consultado em
# 2026-10-19), negada. Fora da lista, de propósito: as ch-* (dicas de cliente, não capacidades da página), as que só
# existem em Isolated Web Apps e as experimentais que, negadas, quebrariam o que a SPA usa (autofill, manual-text,
# focus-without-user-activation, vertical-scroll). Feature que o navegador não conhece gera só um aviso no console.
DENIED_FEATURES = (
    "accelerometer", "ambient-light-sensor", "aria-notify", "bluetooth", "browsing-topics", "camera",
    "captured-surface-control", "clipboard-read", "clipboard-write", "compute-pressure", "cross-origin-isolated",
    "deferred-fetch", "deferred-fetch-minimal", "device-attributes", "digital-credentials-create",
    "digital-credentials-get", "display-capture", "encrypted-media", "execution-while-not-rendered",
    "execution-while-out-of-viewport", "fullscreen", "gamepad", "geolocation", "gyroscope", "haptics", "hid",
    "identity-credentials-get", "idle-detection", "interest-cohort", "keyboard-map", "language-detector",
    "language-model", "local-fonts", "local-network", "local-network-access", "loopback-network", "magnetometer",
    "media-playback-while-not-visible", "midi", "on-device-speech-recognition", "otp-credentials", "payment",
    "picture-in-picture", "private-state-token-issuance", "private-state-token-redemption",
    "publickey-credentials-create", "publickey-credentials-get", "publickey-credentials-remote-client-data-json",
    "rewriter", "screen-wake-lock", "serial", "storage-access", "summarizer", "sync-xhr", "tools", "translator",
    "unbounded-element", "unload", "usb", "web-app-installation", "web-share", "webnn", "window-management",
    "writer", "xr-spatial-tracking",
)

EXPECTED_PERMISSIONS_POLICY = {feature: "(self)" for feature in ALLOWED_FEATURES} | {
    feature: "()" for feature in DENIED_FEATURES}

# A selfie é a única página que liga a câmera, só para a própria origem.
EXPECTED_SELFIE_PERMISSIONS_POLICY = EXPECTED_PERMISSIONS_POLICY | {"camera": "(self)"}


def docker_available() -> bool:
    if shutil.which("docker") is None:
        return False
    return subprocess.run(["docker", "info"], capture_output=True, check=False).returncode == 0


def parse_csp(header: str) -> dict[str, list[str]]:
    """Diretiva -> fontes de um cabeçalho com uma só política; ValueError se houver vírgula ou diretiva repetida.

    O navegador aplica a primeira ocorrência de uma diretiva e ignora as seguintes (CSP3 2.2.1), e a vírgula abre
    outra política (CSP3 3.1): em vez de escolher uma leitura, a política ambígua é recusada.
    """
    if "," in header:
        raise ValueError(f"mais de uma política no mesmo cabeçalho: {header!r}")
    directives = [directive.split() for directive in header.split(";") if directive.strip()]
    return _unique_pairs((tokens[0].lower(), tokens[1:]) for tokens in directives)


def parse_permissions_policy(header: str) -> dict[str, str]:
    """Feature -> allowlist; ValueError se uma feature aparecer duas vezes."""
    members = (member.strip().partition("=") for member in header.split(",") if member.strip())
    return _unique_pairs((name.lower(), allowlist) for name, _, allowlist in members)


def is_exact_policy[T](values: list[str], parse: Callable[[str], dict[str, T]], expected: dict[str, T]) -> bool:
    """True só com um único cabeçalho cuja política, sem ambiguidade, é exatamente a esperada."""
    if len(values) != 1:
        return False
    try:
        return parse(values[0]) == expected
    except ValueError:
        return False


def _unique_pairs[T](pairs: Iterable[tuple[str, T]]) -> dict[str, T]:
    pair_list = list(pairs)
    names = [name for name, _ in pair_list]
    repeated = sorted({name for name in names if names.count(name) > 1})
    if repeated:
        raise ValueError(f"nome repetido na política: {', '.join(repeated)}")
    return dict(pair_list)


def remove_container(name: str) -> None:
    """Remove o container pelo nome exato; sem efeito se ele não chegou a existir."""
    subprocess.run(["docker", "rm", "--force", name], capture_output=True, check=False)
