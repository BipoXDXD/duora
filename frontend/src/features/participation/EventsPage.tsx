import { usePrivateOperation } from '@/features/session/private-operation'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useId, useRef, useState } from 'react'
import { useAccount } from '@/features/session/account-context'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page, TextLink } from '@/shared/ui/page'
import { checkIn, fetchEvents, fetchParticipation, register, withdraw, type Event } from './participation-api'
import { canWithdrawRegistration } from './withdrawal'

export function EventsPage(): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { id } = useAccount()
  const { t } = useI18n()
  const events = useQuery({
    queryKey: ['account', id, 'events'],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchEvents(operation, signal)),
    gcTime: 0,
  })
  return (
    <Page title={t('participation.events')}>
      <TextLink to={paths.preferences}>{t('preferences.title')}</TextLink>
      {events.isError ? (
        <RequestFailureAlert failure={classifyRequestFailure(events.error)} />
      ) : events.data === undefined ? (
        <p role="status">{t('preferences.loading')}</p>
      ) : events.data.length === 0 ? (
        <p>{t('participation.noEvents')}</p>
      ) : (
        events.data.map((event) => <EventEntry key={event.eventId} event={event} userId={id} />)
      )}
      <TextLink to={paths.account}>{t('profile.backToAccount')}</TextLink>
    </Page>
  )
}

/** O aviso de rodada cancelada ocupa o lugar do "aguardando" na mesma região `status`: anuncia sem tirar o foco. */
function statusMessage(
  state: string | undefined,
  roundCancelled: boolean,
): 'participation.roundCancelled' | 'participation.waiting' | 'participation.joinInstructions' {
  if (roundCancelled) {
    return 'participation.roundCancelled'
  }
  return state === 'waiting' || state === 'checkedin' ? 'participation.waiting' : 'participation.joinInstructions'
}

type EntryCommand = 'register' | 'checkIn' | 'withdraw'

const NOT_FOUND = 404

/**
 * O servidor recusa a retirada com 404 (API-E04) depois do início, com o evento fora de Published ou com a inscrição
 * em outro estado: a tela avisa que a retirada fechou, sem afirmar o motivo, em vez do alerta genérico.
 */
function withdrawalRefused(error: Error | null): boolean {
  const failure = classifyRequestFailure(error)
  return failure.kind === 'problem' && failure.status === NOT_FOUND
}

function sendCommand(operation: Parameters<typeof register>[0], eventId: string, action: EntryCommand): Promise<void> {
  switch (action) {
    case 'register':
      return register(operation, eventId)
    case 'checkIn':
      return checkIn(operation, eventId)
    case 'withdraw':
      return withdraw(operation, eventId)
  }
}

/** Onde o foco vai depois da próxima renderização da retirada: o botão, o aviso da confirmação ou o resultado. */
type WithdrawalFocus = 'button' | 'warning' | 'outcome'

function EventEntry({ event, userId }: { readonly event: Event; readonly userId: string }): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { t, formatEventDateTime } = useI18n()
  const client = useQueryClient()
  const warningId = useId()
  const [confirmingWithdrawal, setConfirmingWithdrawal] = useState(false)
  const withdrawButton = useRef<HTMLButtonElement>(null)
  const warning = useRef<HTMLParagraphElement>(null)
  const outcome = useRef<HTMLParagraphElement>(null)
  const closedNotice = useRef<HTMLParagraphElement>(null)
  // A retirada fechou com a confirmação aberta: o encontro começou na tela ou o servidor recusou com 404.
  const [withdrawClosed, setWithdrawClosed] = useState(false)
  const pendingFocus = useRef<WithdrawalFocus | null>(null)
  const key = ['account', userId, 'participation', event.eventId]
  const participation = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchParticipation(operation, event.eventId, signal)),
    gcTime: 0,
    refetchInterval: 3000,
  })
  const command = usePrivateMutation({
    mutationFn: (operation, action: EntryCommand) => sendCommand(operation, event.eventId, action),
    onSuccess: async (_, action) => {
      if (action === 'withdraw') {
        pendingFocus.current = 'outcome'
      }
      setConfirmingWithdrawal(false)
      await client.invalidateQueries({ queryKey: key })
    },
    onError: (error, action) => {
      if (action === 'withdraw' && withdrawalRefused(error)) {
        setConfirmingWithdrawal(false)
        setWithdrawClosed(true)
      }
    },
  })
  const state = participation.data?.state.toLowerCase()
  const withdrawn = state === 'withdrawn'
  const roundCancelled = participation.data?.roundCancelled === true
  // Com check-in feito, inscrição e presença já não têm o que fazer: o aviso de status diz o que vem a seguir.
  const awaitingRound = state === 'waiting' || state === 'checkedin' || roundCancelled
  // O instante da última leitura do estado: puro na renderização e renovado pelo polling de 3 s.
  const canWithdraw = canWithdrawRegistration(state, event.startsAt, participation.dataUpdatedAt)
  if (confirmingWithdrawal && !canWithdraw) {
    // O encontro começou (ou a inscrição mudou) com a confirmação aberta: ela fecha, como o servidor recusaria.
    setConfirmingWithdrawal(false)
    setWithdrawClosed(true)
  }
  useEffect(() => {
    // A confirmação sumiu com o foco dentro dela; o aviso que a substitui diz por quê.
    if (withdrawClosed && document.activeElement === document.body) {
      closedNotice.current?.focus()
    }
  }, [withdrawClosed])
  const waitlisted = state === 'waitlisted'
  const withdrawLabel = t(waitlisted ? 'participation.leaveWaitlist' : 'participation.withdraw')
  const commandFailed = command.isError && !(command.variables === 'withdraw' && withdrawalRefused(command.error))
  useEffect(() => {
    const focus = pendingFocus.current
    if (focus === null) {
      return
    }
    const target = { button: withdrawButton, warning, outcome }[focus].current
    if (target !== null) {
      pendingFocus.current = null
      target.focus()
    }
  })
  return (
    <section className="flex flex-col gap-4 rounded-lg border border-border p-5">
      <h2 className="text-xl font-semibold">{event.title}</h2>
      <p>
        <time className="whitespace-pre-line" dateTime={event.startsAt}>
          {formatEventDateTime(new Date(event.startsAt), event.timeZone)}
        </time>
      </p>
      {participation.isError ? <RequestFailureAlert failure={classifyRequestFailure(participation.error)} /> : null}
      {commandFailed ? <RequestFailureAlert failure={classifyRequestFailure(command.error)} /> : null}
      {participation.data === undefined ? (
        participation.isError ? null : (
          <p role="status">{t('participation.loadingStatus')}</p>
        )
      ) : participation.data.roomId === null ? (
        withdrawn ? (
          <p ref={outcome} role="status" tabIndex={-1}>
            {t('participation.withdrawn')}
          </p>
        ) : (
          <>
            <p role="status">{t(statusMessage(state, roundCancelled))}</p>
            {awaitingRound ? null : (
              <>
                <Button
                  disabled={command.isPending}
                  onClick={() => {
                    command.mutate('register')
                  }}
                >
                  {t('participation.register')}
                </Button>
                <Button
                  disabled={command.isPending}
                  onClick={() => {
                    command.mutate('checkIn')
                  }}
                >
                  {t('participation.checkIn')}
                </Button>
              </>
            )}
            {withdrawClosed ? (
              <p ref={closedNotice} role="status" tabIndex={-1}>
                {t('participation.withdrawClosed')}
              </p>
            ) : null}
            {canWithdraw && !confirmingWithdrawal && !withdrawClosed ? (
              <Button
                ref={withdrawButton}
                variant="secondary"
                disabled={command.isPending}
                onClick={() => {
                  pendingFocus.current = 'warning'
                  setConfirmingWithdrawal(true)
                }}
              >
                {withdrawLabel}
              </Button>
            ) : null}
            {canWithdraw && confirmingWithdrawal ? (
              <div role="group" aria-label={withdrawLabel} className="flex flex-col gap-3">
                <p id={warningId} ref={warning} tabIndex={-1}>
                  {t('participation.withdrawWarning')}
                </p>
                <Button
                  variant="neutral"
                  disabled={command.isPending}
                  onClick={() => {
                    pendingFocus.current = 'button'
                    setConfirmingWithdrawal(false)
                  }}
                >
                  {t(waitlisted ? 'participation.stayOnWaitlist' : 'participation.keepRegistration')}
                </Button>
                <Button
                  variant="destructive"
                  aria-describedby={warningId}
                  disabled={command.isPending}
                  onClick={() => {
                    command.mutate('withdraw')
                  }}
                >
                  {t(waitlisted ? 'participation.confirmLeaveWaitlist' : 'participation.confirmWithdraw')}
                </Button>
              </div>
            ) : null}
          </>
        )
      ) : (
        <TextLink to={`/rooms/${encodeURIComponent(participation.data.roomId)}`}>
          {t('participation.openRoom')}
        </TextLink>
      )}
    </section>
  )
}
