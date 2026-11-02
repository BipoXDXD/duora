import { usePublicMutation } from '@/features/session/public-operation'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { requestGoogle, useGoogleOptions } from './google-api'

export function GoogleAuthButton({ purpose }: { readonly purpose: 'Login' | 'Register' }): React.JSX.Element | null {
  const { t } = useI18n()
  const options = useGoogleOptions()
  const request = usePublicMutation({
    mutationFn: (operation) => requestGoogle(operation, purpose),
    onSuccess: (url) => {
      window.location.assign(url)
    },
    gcTime: 0,
  })
  if (!options.data?.enabled || (purpose === 'Register' && !options.data.registrationEnabled)) return null
  return (
    <>
      {request.isError ? <RequestFailureAlert failure={classifyRequestFailure(request.error)} /> : null}
      <Button
        variant="secondary"
        aria-disabled={request.isPending}
        onClick={() => {
          if (!request.isPending) request.mutate()
        }}
      >
        {t('google.continue')}
      </Button>
    </>
  )
}
