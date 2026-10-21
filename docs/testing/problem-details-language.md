# Problem Details — issue #104

API 8.1/8.6: `ApiProblems.TitleFor` fixa o texto em inglês por `code`, independente do idioma HTTP.
Endpoints não recebem mais um argumento de título livre. Status, code, type, traceId, Retry-After
 e códigos de validação por campo permanecem iguais. `detail` não é emitido; a customização dos
problemas do framework também substitui título/detalhe arbitrários, inclusive com code já preenchido.
Códigos desconhecidos preservam sua identidade com título seguro `Request failed.`.

## Inventário de emissão

- `ApiProblems.Problem`: autenticação/MFA, autorização/CSRF, perfil/preferências/fuso, participação,
  catálogos, fotos, indisponibilidade de banco/conta, selfie e rotas etárias retiradas.
- `ApiProblems.Validation`: erros de campos continuam códigos (REQUIRED, INVALID_FORMAT etc.).
- `AddDefaultCode`: JSON inválido, desafios 401/403, 404/405 e exceções 500 do framework.
- O contrato OpenAPI e os tipos gerados não mudam: não há endpoint, DTO, status ou code novo.
- Descrições internas de OpenAPI e documentação geral não são traduzidas; isso pertence à #106.

## Contrato de títulos

| Code | Title |
|---|---|
| `ACCOUNT_NOT_ELIGIBLE` | The account is not eligible. |
| `ACCOUNT_RESTRICTED` | The account is restricted. |
| `AGE_VERIFICATION_NOT_AVAILABLE` | Age verification is not available. |
| `AGE_VERIFICATION_UNAVAILABLE` | Age verification is unavailable. |
| `AGE_VERIFICATION_RETIRED` | Age verification has been retired. |
| `CSRF_VALIDATION_FAILED` | Request verification failed. |
| `IDEMPOTENCY_KEY_REUSED` | The idempotency key was already used for a different request. |
| `IDEMPOTENCY_REQUEST_IN_PROGRESS` | The request is still in progress. |
| `INVALID_CREDENTIALS` | Incorrect credentials. |
| `INVALID_REQUEST` | Invalid request. |
| `INVALID_TOKEN` | Invalid or expired token. |
| `LENGTH_REQUIRED` | Content length is required. |
| `MEDIA_QUEUE_FULL` | The media processing queue is full. |
| `MEDIA_UPLOADS_DISABLED` | Media uploads are disabled. |
| `MFA_REQUIRED` | Two-factor authentication is required. |
| `PASSWORD_CHANGE_REQUIRED` | A password change is required. |
| `PAYLOAD_TOO_LARGE` | The request payload is too large. |
| `PRECONDITION_FAILED` | The resource has changed. Reload before saving. |
| `PRECONDITION_REQUIRED` | A precondition is required. |
| `MFA_CODE_INVALID` | Invalid two-factor authentication code. |
| `MFA_ALREADY_ENABLED` | Two-factor authentication is already enabled. |
| `MFA_ENROLLMENT_NOT_STARTED` | Two-factor authentication setup has not started. |
| `MFA_ENROLLMENT_REQUIRED` | Two-factor authentication setup is required. |
| `MFA_NOT_ENABLED` | Two-factor authentication is not enabled. |
| `MFA_REQUIRED_BY_ROLE` | This role requires two-factor authentication. |
| `REAUTHENTICATION_REQUIRED` | Reauthentication is required. |
| `RATE_LIMITED` | Too many attempts. Try again later. |
| `SELF_BLOCK` | You cannot block yourself. |
| `STORAGE_UNAVAILABLE` | Storage is temporarily unavailable. |
| `TEMPORARILY_UNAVAILABLE` | Temporarily unavailable. |
| `UNSUPPORTED_MEDIA_TYPE` | Unsupported media type. |
| `UPLOAD_INCOMPLETE` | The upload is incomplete. |
| `UPLOAD_PENDING_LIMIT` | Too many uploads are pending. |
| `VALIDATION_FAILED` | The request contains invalid fields. |
| `WEBHOOK_PAYLOAD_INVALID` | Invalid webhook payload. |
| `WEBHOOK_SIGNATURE_INVALID` | Invalid webhook signature. |
| `UNAUTHENTICATED` | Authentication required. |
| `FORBIDDEN` | Access denied. |
| `NOT_FOUND` | Resource not found. |
| `METHOD_NOT_ALLOWED` | Method not allowed. |
| `INTERNAL_ERROR` | An internal error occurred. |
| `SELFIE_UNAVAILABLE` | Selfie verification is unavailable. |
| `VERSION_CONFLICT` | The resource version has changed. Reload before continuing. |

## Evidências e limites

`ProblemTextTests` enumera títulos literais, confere cobertura das constantes e normalização do
framework, sem substituir a verificação por ausência de acentos. `ProblemAssert` verifica em todas
as respostas de erro exercitadas na integração o título canônico e ausência de detail.
Testes da interface e Playwright enviam canários em title/detail, usam códigos conhecidos e
desconhecidos em PT-BR/en-US e exigem tradução/fallback local e ausência do canário. Axe acompanha
os cenários Playwright. A API desses cenários de navegador é simulada; não é prova PostgreSQL.

Uma sonda local compilando a própria fonte ApiProblems reproduziu o título arbitrário antes da
correção e passou depois. A suíte PostgreSQL deve ser confirmada no CI enquanto o Docker local
estiver indisponível. Nenhuma exceção de scanner, workflow, autorização ou política foi alterada.
Rollback é de binários; não há migração ou alteração persistida.
