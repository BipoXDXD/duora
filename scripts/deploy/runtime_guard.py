"""Read-only identity checks; no enrollment, Docker access or repair on import."""


class GuardError(ValueError):
    """Refuse before worker shutdown or migration without echoing host metadata."""


def container_identity(metadata: dict) -> dict:
    raise NotImplementedError("Versioned stable container identity without plaintext environment")


def volume_identity(metadata: dict) -> dict:
    raise NotImplementedError("Stable named volume identity")


def require_preserved(baseline: dict, observed: dict) -> None:
    raise NotImplementedError("Compare reviewed sealed baseline without enrolling or repairing")
