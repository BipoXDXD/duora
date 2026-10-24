import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useParams } from 'react-router'
import { useAccount } from '@/features/session/account-context'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page, TextLink } from '@/shared/ui/page'
import { useGameFocus } from './use-game-focus'
import { ExperienceDeadline } from './ExperienceDeadline'
import { RoutesGamePanel } from './RoutesGamePanel'
import { act, actOnRoutes, fetchRoom, roomCommand, type RoutesAction, type Room } from './participation-api'

export function RoomPage(): React.JSX.Element {
  const { roomId = '' } = useParams()
  const { id } = useAccount()
  const { t } = useI18n()
  const client = useQueryClient()
  const key = ['account', id, 'room', roomId]
  const room = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => fetchRoom(roomId, signal),
    gcTime: 0,
    refetchInterval: 2000,
  })
  const refresh = async () => {
    await client.invalidateQueries({ queryKey: key })
  }
  return (
    <Page title={t('participation.room')}>
      {room.isError ? (
        <RequestFailureAlert failure={classifyRequestFailure(room.error)} />
      ) : room.data === undefined ? (
        <p role="status">{t('preferences.loading')}</p>
      ) : (
        <RoomBody room={room.data} refresh={refresh} />
      )}
      <TextLink to={paths.events}>{t('participation.backToEvents')}</TextLink>
    </Page>
  )
}
function RoomBody({
  room,
  refresh,
}: {
  readonly room: Room
  readonly refresh: () => Promise<void>
}): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  const command = useMutation({
    mutationFn: (action: 'accept' | 'reject' | 'exit') => roomCommand(room.roomId, action),
    onSuccess: refresh,
  })
  const action = useMutation({
    mutationFn: (choice: { type: 'choose-option' | 'skip-card'; cardId: string; optionId?: string }) =>
      act(room, choice),
    onSuccess: refresh,
    onError: refresh,
  })
  const routesAction = useMutation({
    mutationFn: (choice: RoutesAction) => actOnRoutes(room, choice),
    onSuccess: refresh,
    onError: refresh,
  })
  const state = room.state.toLowerCase()
  const invited = state === 'invited' || state === 'ready'
  const active = state === 'active'
  const gamePanel = useGameFocus(active)
  const completed = state === 'completed'
  const expiredGame = state === 'expired' && (room.game !== null || room.routesGame !== null)
  const closed = !invited && !active && !completed && !expiredGame
  const deadline = room.game?.deadline ?? room.routesGame?.deadline
  const busy = command.isPending || action.isPending || routesAction.isPending
  const card = room.game?.currentCard
  return (
    <>
      <Alert variant="info">
        {t('participation.agreedLanguage', {
          language: t(room.communicationLanguage === 'en' ? 'preferences.english' : 'preferences.portuguese'),
        })}
      </Alert>
      <p>{t('preferences.noTranslation')}</p>
      <p>{t('preferences.voiceOptional')}</p>
      {command.isError ? <RequestFailureAlert failure={classifyRequestFailure(command.error)} /> : null}
      {action.isError ? <RequestFailureAlert failure={classifyRequestFailure(action.error)} /> : null}
      {routesAction.isError ? <RequestFailureAlert failure={classifyRequestFailure(routesAction.error)} /> : null}
      {invited ? (
        <section className="flex flex-col gap-4">
          <p>
            {t('participation.invitationDeadline', {
              deadline: formatDateTime(new Date(room.invitationExpiresAt)),
            })}
          </p>
          <Button
            disabled={busy}
            onClick={() => {
              command.mutate('accept')
            }}
          >
            {t('participation.accept')}
          </Button>
          <Button
            variant="secondary"
            disabled={busy}
            onClick={() => {
              command.mutate('reject')
            }}
          >
            {t('participation.reject')}
          </Button>
        </section>
      ) : null}
      {active && deadline !== undefined ? (
        <ExperienceDeadline key={`${room.roomId}:${deadline}`} deadline={deadline} />
      ) : null}
      {expiredGame ? <p role="status">{t('participation.expiredProgress')}</p> : null}
      {completed ? <Alert variant="success">{t('participation.completed')}</Alert> : null}
      {closed ? <p role="status">{t('participation.closed')}</p> : null}
      {room.audio.map((audio) => (
        <section key={audio.cueId} lang={room.displayLocale} className="flex flex-col gap-2">
          <p>{audio.accessibleDescription}</p>
          <audio controls muted preload="none" aria-label={audio.accessibleDescription}>
            <source src={audio.url} type="audio/wav" />
          </audio>
          <p>{audio.transcript}</p>
        </section>
      ))}
      {room.routesGame === null ? null : (
        <RoutesGamePanel
          game={room.routesGame}
          locale={room.displayLocale}
          active={active}
          busy={busy}
          onAction={routesAction.mutate}
        />
      )}
      {room.game === null ? null : (
        <section ref={gamePanel} lang={room.displayLocale} className="flex flex-col gap-4">
          <h2 className="text-xl font-semibold">{room.game.title}</h2>
          <p>{room.game.objective}</p>
          <p role="status" aria-live="polite" aria-atomic="true" className="sr-only">
            {active ? card?.prompt : null}
          </p>
          {room.game.awaitingPartner && active ? <p role="status">{t('participation.awaitingPartner')}</p> : null}
          {active && card !== null && card !== undefined ? (
            <fieldset disabled={busy || room.game.awaitingPartner} className="flex min-w-0 flex-col gap-3">
              <legend className="mb-3 font-semibold">{card.prompt}</legend>
              {card.options.map((option) => (
                <Button
                  key={option.optionId}
                  data-option-id={option.optionId}
                  className="whitespace-normal"
                  onClick={() => {
                    action.mutate({
                      type: 'choose-option',
                      cardId: card.cardId,
                      optionId: option.optionId,
                    })
                  }}
                >
                  {option.label}
                </Button>
              ))}
              <Button
                variant="secondary"
                onClick={() => {
                  action.mutate({ type: 'skip-card', cardId: card.cardId })
                }}
              >
                {t('participation.skip')}
              </Button>
            </fieldset>
          ) : null}
          {room.game.revealed.map((item) => (
            <section key={item.cardId}>
              <h3 className="font-semibold">{item.prompt}</h3>
              <p>
                {t('participation.myChoice')}: {item.myOptionLabel}
              </p>
              <p>
                {t('participation.partnerChoice')}: {item.partnerOptionLabel}
              </p>
            </section>
          ))}
          {room.game.passed.map((item) => (
            <p key={item.cardId}>
              {item.prompt} — {t('participation.passed')}
            </p>
          ))}
        </section>
      )}
      {active ? (
        <Button
          variant="secondary"
          disabled={busy}
          onClick={() => {
            command.mutate('exit')
          }}
        >
          {t('participation.exit')}
        </Button>
      ) : null}
    </>
  )
}
