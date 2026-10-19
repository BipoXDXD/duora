import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useAccount } from '@/features/session/account-context'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import type { PlainMessageKey } from '@/shared/i18n/catalog'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page, TextLink } from '@/shared/ui/page'
import { fetchAgeStatus, safeFlowUrl, startAgeVerification, type AgeStatus } from './age-verification-api'

const STATUS_MESSAGES: Record<AgeStatus['nextStep'], PlainMessageKey> = {
  None: 'age.done',
  StartVerification: 'age.intro',
  WaitForProvider: 'age.wait',
  ContinueWithProvider: 'age.wait',
  WaitForReview: 'age.review',
  NotAvailable: 'age.unavailable',
}
const PROBLEMS = {
  AGE_VERIFICATION_UNAVAILABLE: 'age.providerUnavailable',
  AGE_VERIFICATION_NOT_AVAILABLE: 'age.unavailable',
} as const

/** Captura e avaliação pertencem ao fornecedor; nunca aceitamos resultado pelo navegador. */
export function AgeVerificationPage(): React.JSX.Element {
  const { t } = useI18n()
  const account = useAccount()
  const client = useQueryClient()
  const queryKey = ['age-verification', account.id] as const
  const status = useQuery({
    queryKey,
    queryFn: ({ signal }) => fetchAgeStatus(signal),
    gcTime: 0,
    refetchInterval: (query) => (!query.state.error && query.state.data?.nextStep === 'WaitForProvider' ? 5000 : false),
  })
  const start = useMutation({
    mutationFn: startAgeVerification,
    onSuccess: (data) => {
      client.setQueryData(queryKey, data)
    },
  })
  const failure = status.error ?? start.error
  const current = status.isError ? undefined : status.data
  const flow = current?.nextStep === 'ContinueWithProvider' ? safeFlowUrl(current.continueUrl) : null

  return (
    <Page title={t('age.title')} intro={t('age.intro')}>
      <p>{t('age.guidance')}</p>
      <p>{t('age.privacy')}</p>
      {failure ? <RequestFailureAlert failure={classifyRequestFailure(failure)} problemMessages={PROBLEMS} /> : null}
      {status.isPending ? <p role="status">{t('age.loading')}</p> : null}
      {current && current.nextStep !== 'StartVerification' ? (
        <Alert variant="info">{t(STATUS_MESSAGES[current.nextStep])}</Alert>
      ) : null}
      {current?.nextStep === 'StartVerification' ? (
        <Button
          disabled={start.isPending}
          onClick={() => {
            if (!start.isPending) start.mutate()
          }}
        >
          {t(start.isPending ? 'age.starting' : 'age.start')}
        </Button>
      ) : null}
      {flow ? (
        <a href={flow} rel="noreferrer" className="text-action underline">
          {t('age.continue')}
        </a>
      ) : null}
      {current?.nextStep === 'ContinueWithProvider' && flow === null ? (
        <Alert variant="error">{t('age.providerUnavailable')}</Alert>
      ) : null}
      <Button
        variant="secondary"
        disabled={status.isFetching}
        onClick={() => {
          void status.refetch()
        }}
      >
        {t('age.refresh')}
      </Button>
      <p>
        <TextLink to={paths.account}>{t('age.back')}</TextLink>
      </p>
    </Page>
  )
}
