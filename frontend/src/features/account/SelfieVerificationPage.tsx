import { usePrivateOperation } from '@/features/session/private-operation'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { lazy, Suspense, useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useAccount } from '@/features/session/account-context'
import { ApiProblemError, classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page, TextLink } from '@/shared/ui/page'
import { paths } from '@/shared/paths'
import {
  captureSelfie,
  completeSelfie,
  currentSelfie,
  SELFIE_ATTEMPT_PROBLEMS,
  startSelfie,
  type SelfieAttempts,
  type SelfieCapture as Capture,
} from './selfie-verification-api'
import { isSelfieDocument, loadSelfieDocument } from './selfie-document'

const Camera = lazy(() => import('./SelfieCapture'))
const COPY = {
  'pt-BR': {
    title: 'Confirmar presença',
    intro: 'A selfie verifica qualidade de captura e presença. Não estimamos sua idade nem pedimos documentos.',
    guidance:
      'Centralize o rosto, procure boa iluminação e mantenha a imagem nítida. A captura não será sua foto pública.',
    start: 'Iniciar captura',
    capture: 'Abrir câmera',
    waiting: 'Aguardando verificação…',
    done: 'Presença confirmada.',
    retry: 'Não foi possível concluir. Ajuste a iluminação e o enquadramento antes de tentar novamente.',
    help: 'Sem câmera compatível ou com dificuldade de acessibilidade? Você pode cancelar e tentar outro dispositivo. Nenhum documento será solicitado.',
    back: 'Voltar à conta',
    loading: 'Preparando a página de captura…',
  },
  'en-US': {
    title: 'Confirm presence',
    intro:
      'Your selfie checks capture quality and presence. We do not estimate your age or request identity documents.',
    guidance:
      'Center your face, use good lighting and keep the image clear. This capture will not become your public photo.',
    start: 'Start capture',
    capture: 'Open camera',
    waiting: 'Waiting for verification…',
    done: 'Presence confirmed.',
    retry: 'The check could not be completed. Adjust lighting and framing before trying again.',
    help: 'No compatible camera or an accessibility difficulty? You can cancel and try another device. We will not request documents.',
    back: 'Back to account',
    loading: 'Preparing the capture page…',
  },
} as const

export function SelfieVerificationPage(): React.JSX.Element {
  const account = useAccount()
  const { locale } = useI18n()
  // A câmera só é liberada pela borda no documento carregado nesta rota; vindo de navegação interna, recarrega.
  const [ownDocument] = useState(isSelfieDocument)
  useEffect(() => {
    if (!ownDocument) loadSelfieDocument()
  }, [ownDocument])
  if (!ownDocument) {
    return (
      <Page title={COPY[locale].title}>
        <p role="status">{COPY[locale].loading}</p>
      </Page>
    )
  }
  return <SelfieJourney key={account.id} userId={account.id} />
}

function SelfieJourney({ userId }: { readonly userId: string }): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { locale } = useI18n()
  const copy = COPY[locale]
  const [capture, setCapture] = useState<(Capture & { attemptId: string }) | null>(null)
  const [captureFailed, setCaptureFailed] = useState(false)
  const query = useQuery({
    queryKey: ['selfie', userId],
    queryFn: ({ signal }) => privateOperation.run((operation) => currentSelfie(operation, signal)),
    refetchInterval: (q) => (q.state.data?.nextStep === 'Wait' || q.state.data?.nextStep === 'Capture' ? 2000 : false),
  })
  const start = usePrivateMutation({
    mutationFn: (operation) => startSelfie(operation),
    onSuccess: async () => {
      setCapture(null)
      setCaptureFailed(false)
      await query.refetch()
    },
    onError: async (failure) => {
      if (isAttemptLimit(failure)) await query.refetch()
    },
  })
  const open = usePrivateMutation({
    mutationFn: (operation, id: string) => captureSelfie(operation, id),
    onSuccess: (session, attemptId) => {
      setCaptureFailed(false)
      setCapture({ ...session, attemptId })
    },
  })
  const startError = start.error && !isAttemptLimit(start.error) ? start.error : null
  const error = query.error ?? startError ?? open.error
  const status = query.data
  const attempts = status?.attempts
  const blocked = attempts?.state === 'CoolingDown' || attempts?.state === 'Exhausted'
  return (
    <Page title={copy.title}>
      <p>{copy.intro}</p>
      <p>{copy.guidance}</p>
      <p>{copy.help}</p>
      {error ? <RequestFailureAlert failure={classifyRequestFailure(error)} /> : null}
      {(captureFailed || status?.nextStep === 'Recapture') && !blocked ? (
        <Alert variant="info">{copy.retry}</Alert>
      ) : null}
      {attempts && status.state !== 'Passed' ? <AttemptsNotice attempts={attempts} /> : null}
      {status?.state === 'Passed' ? <Alert variant="success">{copy.done}</Alert> : null}
      {query.isPending || status?.nextStep === 'Wait' ? <p role="status">{copy.waiting}</p> : null}
      {status && ['Start', 'Recapture'].includes(status.nextStep) && !blocked ? (
        <Button
          disabled={start.isPending}
          onClick={() => {
            start.mutate()
          }}
        >
          {copy.start}
        </Button>
      ) : null}
      {status?.nextStep === 'Capture' && status.id && capture?.attemptId !== status.id ? (
        <Button
          disabled={open.isPending}
          onClick={() => {
            if (status.id) open.mutate(status.id)
          }}
        >
          {copy.capture}
        </Button>
      ) : null}
      {capture && status?.id === capture.attemptId && status.nextStep === 'Capture' ? (
        <Suspense fallback={<p role="status">{copy.waiting}</p>}>
          <Camera
            session={capture}
            onCancel={() => {
              setCapture(null)
              setCaptureFailed(true)
            }}
            onComplete={() =>
              privateOperation.run(async (operation) => {
                setCapture(null)
                try {
                  if (status.id) await completeSelfie(operation, status.id)
                  await query.refetch()
                } catch (completionError) {
                  privateOperation.assertCurrent()
                  if (!(completionError instanceof DOMException && completionError.name === 'AbortError'))
                    setCaptureFailed(true)
                }
              })
            }
          />
        </Suspense>
      ) : null}
      <TextLink to={paths.account}>{copy.back}</TextLink>
    </Page>
  )
}

function isAttemptLimit(failure: Error): boolean {
  return failure instanceof ApiProblemError && SELFIE_ATTEMPT_PROBLEMS.has(failure.problem.code ?? '')
}

/**
 * Limite de reprovações (issue #300). O servidor decide; a tela só conta o que falta. A contagem aparece depois da
 * primeira reprovação, e sempre no segundo ciclo, que termina na suspensão da conta.
 */
function AttemptsNotice({ attempts }: { readonly attempts: SelfieAttempts }): React.JSX.Element | null {
  const { t, formatDateTime } = useI18n()
  switch (attempts.state) {
    case 'Available':
      if (attempts.lastCycle) {
        return <Alert variant="info">{t('selfieAttempts.lastCycle', { remaining: attempts.remaining })}</Alert>
      }
      return attempts.remaining < FULL_CYCLE ? (
        <p role="status">{t('selfieAttempts.firstCycle', { remaining: attempts.remaining })}</p>
      ) : null
    case 'CoolingDown': {
      if (attempts.lockedUntil === null) return null
      const [before, after] = t('selfieAttempts.coolingDown', { time: TIME_SLOT }).split(TIME_SLOT)
      return (
        <Alert variant="info">
          {before}
          <time dateTime={attempts.lockedUntil}>{formatDateTime(new Date(attempts.lockedUntil))}</time>
          {after}
        </Alert>
      )
    }
    case 'Exhausted':
      return <Alert variant="error">{t('selfieAttempts.exhausted')}</Alert>
    case 'Unknown':
      return null
  }
}

/** Tentativas de um ciclo cheio; com todas, ainda não houve reprovação e a tela não mostra contagem. */
const FULL_CYCLE = 5

/** Marcador para pôr o horário num elemento time dentro do texto traduzido. */
const TIME_SLOT = '\u0000time\u0000'
