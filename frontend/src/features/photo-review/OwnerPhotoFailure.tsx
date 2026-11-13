import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { ownerPhotoProblemMessage } from './owner-photo-failure'

export function OwnerPhotoFailure({ error }: { readonly error: unknown }): React.JSX.Element {
  const { t } = useI18n()
  const message = ownerPhotoProblemMessage(error)
  // Vem antes do alerta comum porque o 429 do limite de pendentes não se resolve esperando alguns segundos.
  return message === null ? (
    <RequestFailureAlert failure={classifyRequestFailure(error)} />
  ) : (
    <Alert variant="error">{t(message)}</Alert>
  )
}
