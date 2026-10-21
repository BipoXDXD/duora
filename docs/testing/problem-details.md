# Inventário de Problem Details — issue #104

Base: main `a729c6e1d7f0730fd1ef86952d41506c8291c1ea` em 21/10/2026. Textos do protocolo em inglês; apresentação PT-BR/en-US permanece no catálogo local da SPA. Nenhuma regra de segurança, status ou código foi alterada.

## Produtores de endpoints e handlers

Antes, os produtores abaixo forneciam títulos livres. Agora todos usam `ApiProblems.Problem(status, code)`; variantes do mesmo código convergem no título público da tabela seguinte. Nenhum produtor fornecia `detail` explicitamente.

| Origem (Duora.Web) | Status/código anteriores | Título anterior |
|---|---|---|
| `Security/CsrfProtection.cs` | `StatusCodes.Status403Forbidden, ApiProblems.CsrfValidationFailed` | Requisição recusada. |
| `Security/SessionAuthorization.cs` | `StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable` | Tente novamente em instantes. |
| `Security/SessionAuthorization.cs` | `StatusCodes.Status403Forbidden, ApiProblems.PasswordChangeRequired` | Troque a senha temporária para continuar. |
| `Security/SessionAuthorization.cs` | `StatusCodes.Status403Forbidden, ApiProblems.MfaEnrollmentRequired` | Cadastre o segundo fator para continuar. |
| `Security/StepUp.cs` | `StatusCodes.Status401Unauthorized, ApiProblems.ReauthenticationRequired` | Confirme a senha para continuar. |
| `Safety/BlockEndpoints.cs` | `StatusCodes.Status400BadRequest, ApiProblems.InvalidRequest` | Esta operação não aceita corpo. |
| `Safety/BlockEndpoints.cs` | `StatusCodes.Status400BadRequest, ApiProblems.SelfBlock` | Não é possível bloquear a própria conta. |
| `Safety/BlockEndpoints.cs` | `StatusCodes.Status403Forbidden, ApiProblems.AccountNotEligible` | A conta ainda não pode usar esta função. |
| `Safety/BlockEndpoints.cs` | `StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable` | Não foi possível concluir agora. Tente de novo em instantes. |
| `Accounts/SelfieVerificationEndpoints.cs` | `503, "SELFIE_UNAVAILABLE"` | Selfie verification unavailable. |
| `Accounts/ReauthenticationEndpoints.cs` | `StatusCodes.Status401Unauthorized, ApiProblems.InvalidCredentials` | Senha ou código inválido. |
| `Accounts/AuthEndpoints.cs` | `StatusCodes.Status401Unauthorized, ApiProblems.MfaRequired` | A conta exige um segundo fator. |
| `Accounts/AuthEndpoints.cs` | `StatusCodes.Status401Unauthorized, ApiProblems.InvalidCredentials` | E-mail ou senha inválidos. |
| `Accounts/AuthEndpoints.cs` | `StatusCodes.Status400BadRequest, ApiProblems.InvalidToken` | Link inválido ou expirado. |
| `Accounts/AgeVerificationEndpoints.cs` | `410, "AGE_VERIFICATION_RETIRED"` | Age verification has been retired. |
| `Accounts/AgeVerificationEndpoints.cs` | `410, "AGE_VERIFICATION_RETIRED"` | Age verification has been retired. |
| `Accounts/MfaEndpoints.cs` | `StatusCodes.Status400BadRequest, ApiProblems.MfaCodeInvalid` | Código inválido. |
| `Accounts/MfaEndpoints.cs` | `StatusCodes.Status409Conflict, ApiProblems.MfaEnrollmentNotStarted` | Comece o cadastro do autenticador antes de confirmar. |
| `Accounts/MfaEndpoints.cs` | `StatusCodes.Status403Forbidden, ApiProblems.PasswordChangeRequired` | Troque a senha temporária para continuar. |
| `Accounts/MfaEndpoints.cs` | `StatusCodes.Status403Forbidden, ApiProblems.MfaRequiredByRole` | O papel desta conta exige MFA. |
| `Accounts/MfaEndpoints.cs` | `StatusCodes.Status409Conflict, ApiProblems.MfaAlreadyEnabled` | O MFA já está ativo. |
| `Accounts/MfaEndpoints.cs` | `StatusCodes.Status409Conflict, ApiProblems.MfaNotEnabled` | O MFA não está ativo. |
| `Http/DatabaseUnavailability.cs` | `StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable` | Serviço temporariamente indisponível. Tente novamente em instantes. |
| `Http/AccountBusyExceptionHandler.cs` | `StatusCodes.Status503ServiceUnavailable, ApiProblems.TemporarilyUnavailable` | A conta está ocupada. Tente novamente em instantes. |
| `Profiles/ProfileEndpoints.cs` | `StatusCodes.Status403Forbidden, ApiProblems.AccountRestricted` | A situação da conta não permite alterar o perfil. |
| `Profiles/ProfileEndpoints.cs` | `StatusCodes.Status428PreconditionRequired, ApiProblems.PreconditionRequired` | Envie o ETag da última leitura do perfil no header If-Match. |
| `Profiles/ProfileEndpoints.cs` | `StatusCodes.Status412PreconditionFailed, ApiProblems.PreconditionFailed` | O perfil mudou desde a sua leitura. Recarregue e aplique a alteração de novo. |
| `Profiles/TimeZoneEndpoints.cs` | `428, ApiProblems.PreconditionRequired` | Envie o ETag da última leitura. |
| `Profiles/TimeZoneEndpoints.cs` | `412, ApiProblems.PreconditionFailed` | O recurso mudou. Recarregue antes de salvar. |
| `Profiles/TimeZoneEndpoints.cs` | `403, ApiProblems.AccountRestricted` | A situação da conta não permite esta alteração. |
| `Profiles/PreferenceEndpoints.cs` | `428, ApiProblems.PreconditionRequired` | Envie o ETag da última leitura. |
| `Profiles/PreferenceEndpoints.cs` | `412, ApiProblems.PreconditionFailed` | O recurso mudou. Recarregue antes de salvar. |
| `Profiles/PreferenceEndpoints.cs` | `403, ApiProblems.AccountRestricted` | A situação da conta não permite esta alteração. |
| `Webhooks/WebhookEndpoints.cs` | `410, "AGE_VERIFICATION_RETIRED"` | Age verification has been retired. |
| `Participation/ParticipationEndpoints.cs` | `503, ApiProblems.TemporarilyUnavailable` | Temporarily unavailable. |
| `Participation/ParticipationEndpoints.cs` | `409, "VERSION_CONFLICT"` | Reload the current room. |
| `Participation/ParticipationEndpoints.cs` | `400, "VALIDATION_FAILED"` | Invalid request. |
| `Participation/RoutesCatalogEndpoints.cs` | `400, ApiProblems.ValidationFailed` | Invalid bilingual catalog or audio manifest. |
| `Participation/CatalogEndpoints.cs` | `503, ApiProblems.TemporarilyUnavailable` | Temporarily unavailable. |
| `Participation/CatalogEndpoints.cs` | `403, "FORBIDDEN"` | Forbidden. |
| `Participation/CatalogEndpoints.cs` | `400, ApiProblems.ValidationFailed` | Invalid bilingual catalog or audio manifest. |
| `Media/PhotoEndpoints.cs` | `StatusCodes.Status503ServiceUnavailable, ApiProblems.MediaUploadsDisabled` | O envio de fotos está desligado. |
| `Media/PhotoEndpoints.cs` | `StatusCodes.Status403Forbidden, ApiProblems.AccountRestricted` | A situação da conta não permite enviar fotos. |
| `Media/PhotoEndpoints.cs` | `StatusCodes.Status415UnsupportedMediaType, ApiProblems.UnsupportedMediaType` | Envie a foto como image/jpeg, image/png ou image/webp. |
| `Media/PhotoEndpoints.cs` | `StatusCodes.Status411LengthRequired, ApiProblems.LengthRequired` | Envie o header Content-Length com o tamanho da foto. |
| `Media/PhotoEndpoints.cs` | `StatusCodes.Status413PayloadTooLarge, ApiProblems.PayloadTooLarge` | A foto passa de 5 MiB. |
| `Media/PhotoEndpoints.cs` | `context, InProgressRetryAfter, StatusCodes.Status409Conflict, ApiProblems.IdempotencyRequestInProgress` | O envio com esta chave ainda está em andamento. |
| `Media/PhotoEndpoints.cs` | `StatusCodes.Status409Conflict, ApiProblems.IdempotencyKeyReused` | Esta chave já foi usada com outra foto. |
| `Media/PhotoEndpoints.cs` | `context, PendingLimitRetryAfter, StatusCodes.Status429TooManyRequests, ApiProblems.UploadPendingLimit` | Há fotos demais em processamento. Aguarde o resultado delas. |
| `Media/PhotoEndpoints.cs` | `context, QueueFullRetryAfter, StatusCodes.Status503ServiceUnavailable, ApiProblems.MediaQueueFull` | Muitas fotos em processamento agora. Tente de novo em instantes. |
| `Media/PhotoEndpoints.cs` | `StatusCodes.Status403Forbidden, ApiProblems.AccountRestricted` | A situação da conta não permite enviar fotos. |
| `Media/PhotoEndpoints.cs` | `StatusCodes.Status400BadRequest, ApiProblems.UploadIncomplete` | A foto não chegou inteira. Envie de novo com a mesma chave. |
| `Media/PhotoEndpoints.cs` | `context, StorageRetryAfter, StatusCodes.Status503ServiceUnavailable, ApiProblems.StorageUnavailable` | Não foi possível guardar a foto agora. Envie de novo com a mesma chave. |

## Catálogo permitido

| Código | Título exato |
|---|---|
| `ACCOUNT_NOT_ELIGIBLE` | The account is not eligible for this operation. |
| `ACCOUNT_RESTRICTED` | The account cannot perform this operation. |
| `AGE_VERIFICATION_NOT_AVAILABLE` | Age verification is not available. |
| `AGE_VERIFICATION_UNAVAILABLE` | Age verification is temporarily unavailable. |
| `CSRF_VALIDATION_FAILED` | Request rejected. |
| `IDEMPOTENCY_KEY_REUSED` | This idempotency key has already been used for another request. |
| `IDEMPOTENCY_REQUEST_IN_PROGRESS` | The request with this idempotency key is still in progress. |
| `INVALID_CREDENTIALS` | Invalid credentials. |
| `INVALID_REQUEST` | Invalid request. |
| `INVALID_TOKEN` | Invalid or expired link. |
| `LENGTH_REQUIRED` | Content-Length is required. |
| `MEDIA_QUEUE_FULL` | Photo processing is busy. Try again later. |
| `MEDIA_UPLOADS_DISABLED` | Photo uploads are disabled. |
| `MFA_REQUIRED` | A second authentication factor is required. |
| `PASSWORD_CHANGE_REQUIRED` | Change your temporary password to continue. |
| `PAYLOAD_TOO_LARGE` | Request payload is too large. |
| `PRECONDITION_FAILED` | The resource has changed. Reload it before saving. |
| `PRECONDITION_REQUIRED` | Provide the ETag from the last read in If-Match. |
| `MFA_CODE_INVALID` | Invalid authentication code. |
| `MFA_ALREADY_ENABLED` | MFA is already enabled. |
| `MFA_ENROLLMENT_NOT_STARTED` | Start MFA enrollment before confirming. |
| `MFA_ENROLLMENT_REQUIRED` | Enroll a second authentication factor to continue. |
| `MFA_NOT_ENABLED` | MFA is not enabled. |
| `MFA_REQUIRED_BY_ROLE` | MFA is required for this account role. |
| `REAUTHENTICATION_REQUIRED` | Confirm your credentials to continue. |
| `RATE_LIMITED` | Too many requests. Try again later. |
| `SELF_BLOCK` | You cannot block your own account. |
| `STORAGE_UNAVAILABLE` | Photo storage is temporarily unavailable. Retry with the same key. |
| `TEMPORARILY_UNAVAILABLE` | Service temporarily unavailable. Try again later. |
| `UNSUPPORTED_MEDIA_TYPE` | Unsupported media type. |
| `UPLOAD_INCOMPLETE` | The upload is incomplete. Retry with the same key. |
| `UPLOAD_PENDING_LIMIT` | Too many photos are pending. Wait for processing to finish. |
| `VALIDATION_FAILED` | The request contains invalid fields. |
| `WEBHOOK_PAYLOAD_INVALID` | Invalid webhook payload. |
| `WEBHOOK_SIGNATURE_INVALID` | Invalid webhook signature. |
| `UNAUTHENTICATED` | Authentication required. |
| `FORBIDDEN` | Forbidden. |
| `NOT_FOUND` | Resource not found. |
| `METHOD_NOT_ALLOWED` | Method not allowed. |
| `INTERNAL_ERROR` | An unexpected error occurred. |
| `AGE_VERIFICATION_RETIRED` | Age verification has been retired. |
| `SELFIE_UNAVAILABLE` | Selfie verification unavailable. |
| `VERSION_CONFLICT` | The resource has changed. Reload it before continuing. |

Os códigos de verificação etária desativada e webhook ainda declarados permanecem reservados; a tabela não reativa esses fluxos. O fallback para qualquer código não catalogado é `The request could not be completed.`.

## Framework e validação

- `AddProblemDetails` usa `AddDefaultCode`: 400 INVALID_REQUEST, 401 UNAUTHENTICATED, 403 FORBIDDEN, 404 NOT_FOUND, 405 METHOD_NOT_ALLOWED, 411 LENGTH_REQUIRED, 413 PAYLOAD_TOO_LARGE, 415 UNSUPPORTED_MEDIA_TYPE, 429 RATE_LIMITED e 500 INTERNAL_ERROR. Demais status continuam `HTTP_{status}` (inclusive um 503 sem código de aplicação).
- UseStatusCodePages, desafio/forbid, binding JSON e UseExceptionHandler chegam ao mesmo normalizador. Títulos padrão do ASP.NET eram dependentes do framework; agora vêm do catálogo. `detail` é removido mesmo quando já há código; exceção, SQL e entrada não viram texto público.
- `ApiProblems.Validation` mantém 400, VALIDATION_FAILED e `errors` por campo; o título passa de “A requisição tem campos inválidos.” para “The request contains invalid fields.”. `RequestValidation`, perfil, preferências, fuso e MFA retornam códigos por campo, preservados. Exceção legada: upload retorna `errors.body = ["A foto está vazia."]` e `errors.Idempotency-Key = ["Obrigatório: de 1 a 64 caracteres entre letras, dígitos, '-' e '_'."]`. Esses valores de `errors` são preservados por compatibilidade; não são title/detail nem usados como texto da SPA. Uma migração deles para códigos exige contrato separado. Sem tradução automática nem alteração de descrições OpenAPI.
- `ApiProblems.TooManyRequests` antes emitia “Muitas tentativas. Tente novamente mais tarde.”; agora usa o título inglês de RATE_LIMITED. Rate limit mantém 429/RATE_LIMITED e Retry-After (mínimo 1 segundo); limites de upload mantêm UPLOAD_PENDING_LIMIT/429. Indisponibilidade da aplicação mantém seus códigos/503 e cabeçalhos anteriores.

## Auditoria das páginas

| Consumidor | Apresentação de falha |
|---|---|
| Login | RequestFailureAlert com INVALID_CREDENTIALS/MFA_REQUIRED locais |
| Cadastro e recuperação | RequestFailureAlert e validação local Zod |
| Confirmação e redefinição | RequestFailureAlert com INVALID_TOKEN local |
| Troca inicial de senha | allowlist SAME_AS_TEMPORARY_PASSWORD; demais códigos via RequestFailureAlert |
| Cadastro MFA | RequestFailureAlert com mapa local explícito |
| Conta, sessões e sair/entrar novamente | RequestFailureAlert; sessão expirada vira navegação/aviso local |
| Perfil | RequestFailureAlert; conflitos/precondições com avisos locais |
| Preferências, idioma e fuso | RequestFailureAlert; conflitos/precondições com avisos locais |
| Eventos e sala (incluindo comandos dos jogos) | RequestFailureAlert |
| Selfie | RequestFailureAlert; falha de câmera com texto local |
| Guarda de sessão e seletor de idioma | RequestFailureAlert |
| Error boundary | mensagem local fixa, sem renderizar Error.message |
| Landing, check-email, not-found | textos locais; não apresentam corpo de erro da API |

`problem-details.ts` e `ApiProblemError` preservam dados para diagnóstico/contrato; nenhuma página renderiza title/detail/Error.message do servidor. Os títulos de eventos/jogos são conteúdo de sucesso e não Problem Details. A única consulta de errors de servidor é uma comparação explícita em ChangePasswordPage, sem eco do valor. A seleção de mensagens agora exige propriedade própria do mapa: constructor/__proto__/toString não escapam do fallback.

## Evidência automatizada

- ProblemTextTests enumera títulos literais, validação, Retry-After e códigos do framework; canários verificam remoção de title/detail e preservação de status/code/traceId.
- ProblemAssert exige título exato e ausência de detail em todas as respostas HTTP verificadas pela suíte existente, incluindo autenticação, validação, uploads, indisponibilidade e erros 500.
- problem-canary.test.tsx percorre parser → erro → classificador → alerta em ambos os idiomas, com códigos desconhecidos e propriedades herdadas.
- problem-details.spec.ts exercita páginas no bundle de produção, com texto inesperado e HTML potencialmente perigoso em title/detail/errors, exigindo mensagem local e ausência de canário no DOM. Ambos os idiomas e os navegadores da configuração padrão estão incluídos.

## Reversão e limites

Reverter o commit restaura os títulos anteriores; não há migração, flag ou implantação. A compatibilidade de schema é verificada pela regeneração e comparação de OpenAPI/tipos. A garantia de apresentação se refere aos caminhos inventariados e aos testes descritos; novas páginas devem manter a fronteira RequestFailure e estender os canários.

## Ampliação de cobertura — issue #142

A [matriz de mutações e limites](problem-details-mutations.md) registra os canários adicionais de
preferências, idioma, fuso, MFA, selfie, eventos e comandos de sala/jogos, em PT-BR/en-US.
