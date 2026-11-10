import { usePrivateOperation } from '@/features/session/private-operation'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useAccount } from '@/features/session/account-context'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page, TextLink } from '@/shared/ui/page'
import { checkIn, fetchEvents, fetchParticipation, register, withdraw, type Event } from './participation-api'

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
type EntryCommand = 'register' | 'checkIn' | 'withdraw'

/** Estados de `GET /participation` em que a pessoa ainda tem inscrição ativa e pode retirá-la (API-E04). */
const WITHDRAWABLE_STATES: ReadonlySet<string> = new Set(['confirmed', 'waitlisted', 'checkedin', 'waiting'])

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

function EventEntry({ event, userId }: { readonly event: Event; readonly userId: string }): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { t, formatEventDateTime } = useI18n()
  const client = useQueryClient()
  const [confirmingWithdrawal, setConfirmingWithdrawal] = useState(false)
  const withdrawButton = useRef<HTMLButtonElement>(null)
  const returnFocus = useRef(false)
  useEffect(() => {
    if (!confirmingWithdrawal && returnFocus.current) {
      returnFocus.current = false
      withdrawButton.current?.focus()
    }
  }, [confirmingWithdrawal])
  const key = ['account', userId, 'participation', event.eventId]
  const participation = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchParticipation(operation, event.eventId, signal)),
    gcTime: 0,
    refetchInterval: 3000,
  })
  const command = usePrivateMutation({
    mutationFn: (operation, action: EntryCommand) => sendCommand(operation, event.eventId, action),
    onSuccess: async () => {
      setConfirmingWithdrawal(false)
      await client.invalidateQueries({ queryKey: key })
    },
  })
  const state = participation.data?.state.toLowerCase()
  const withdrawn = state === 'withdrawn'
  const canWithdraw = state !== undefined && WITHDRAWABLE_STATES.has(state)
  return (
    <section className="flex flex-col gap-4 rounded-lg border border-border p-5">
      <h2 className="text-xl font-semibold">{event.title}</h2>
      <p>
        <time className="whitespace-pre-line" dateTime={event.startsAt}>
          {formatEventDateTime(new Date(event.startsAt), event.timeZone)}
        </time>
      </p>
      {participation.isError ? <RequestFailureAlert failure={classifyRequestFailure(participation.error)} /> : null}
      {command.isError ? <RequestFailureAlert failure={classifyRequestFailure(command.error)} /> : null}
      {participation.data?.roomId === null || participation.data?.roomId === undefined ? (
        withdrawn ? (
          <p role="status">{t('participation.withdrawn')}</p>
        ) : (
          <>
            <p role="status">
              {t(
                state === 'waiting' || state === 'checkedin'
                  ? 'participation.waiting'
                  : 'participation.joinInstructions',
              )}
            </p>
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
            {canWithdraw && !confirmingWithdrawal ? (
              <Button
                ref={withdrawButton}
                variant="secondary"
                disabled={command.isPending}
                onClick={() => {
                  setConfirmingWithdrawal(true)
                }}
              >
                {t(state === 'waitlisted' ? 'participation.leaveWaitlist' : 'participation.withdraw')}
              </Button>
            ) : null}
            {canWithdraw && confirmingWithdrawal ? (
              <div
                role="group"
                aria-label={t(state === 'waitlisted' ? 'participation.leaveWaitlist' : 'participation.withdraw')}
                className="flex flex-col gap-3"
              >
                <p>{t('participation.withdrawWarning')}</p>
                <Button
                  variant="destructive"
                  disabled={command.isPending}
                  onClick={() => {
                    command.mutate('withdraw')
                  }}
                >
                  {t('participation.confirmWithdraw')}
                </Button>
                <Button
                  variant="neutral"
                  disabled={command.isPending}
                  onClick={() => {
                    returnFocus.current = true
                    setConfirmingWithdrawal(false)
                  }}
                >
                  {t('participation.keepRegistration')}
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
