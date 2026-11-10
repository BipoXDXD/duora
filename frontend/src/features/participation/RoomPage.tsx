import { useState } from 'react'
import { PrivateAudioPlayer } from './PrivateAudioPlayer'
import { usePrivateOperation } from '@/features/session/private-operation'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useParams } from 'react-router'
import { useAccount } from '@/features/session/account-context'
import { classifyRequestFailure, type RequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page, TextLink } from '@/shared/ui/page'
import { useGameFocus } from './use-game-focus'
import { ExperienceDeadline } from './ExperienceDeadline'
import { RoutesGamePanel } from './RoutesGamePanel'
import {
  act,
  actOnRoutes,
  readRoom,
  roomCommand,
  type RoomRead,
  type RoutesAction,
  type Room,
} from './participation-api'

const NOT_FOUND = 404

export function RoomPage(): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { roomId = '' } = useParams()
  const { id } = useAccount()
  const { t } = useI18n()
  const client = useQueryClient()
  const key = ['account', id, 'room', roomId]
  const [exited, setExited] = useState(false)
  const room = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => privateOperation.run((operation) => readRoom(operation, roomId, signal)),
    gcTime: 0,
    refetchInterval: 2000,
    enabled: !exited,
  })
  const refresh = async () => {
    await client.invalidateQueries({ queryKey: key })
  }
  if (exited) {
    return (
      <Page title={t('participation.room')}>
        <p role="status">{t('participation.closed')}</p>
        <TextLink to={paths.events}>{t('participation.backToEvents')}</TextLink>
      </Page>
    )
  }
  const read: RoomRead | undefined = room.isError
    ? { kind: 'failed', failure: classifyRequestFailure(room.error) }
    : room.data
  return (
    <Page title={t('participation.room')}>
      {read === undefined ? (
        <p role="status">{t('preferences.loading')}</p>
      ) : read.kind === 'failed' ? (
        <RoomFailure failure={read.failure} />
      ) : (
        <RoomBody room={read.room} refresh={refresh} />
      )}
      {read?.kind === 'failed' ? (
        <ExitAfterForbiddenRead
          roomId={roomId}
          failure={read.failure}
          onExited={() => {
            setExited(true)
          }}
        />
      ) : null}
      <TextLink to={paths.events}>{t('participation.backToEvents')}</TextLink>
    </Page>
  )
}
/** 404 é o aviso neutro de sala indisponível: não distingue sala inexistente de encerrada, nem o motivo (TM-24). */
function RoomFailure({ failure }: { readonly failure: RequestFailure }): React.JSX.Element {
  const { t } = useI18n()
  if (failure.kind === 'problem' && failure.status === NOT_FOUND) {
    return <p role="status">{t('participation.unavailable')}</p>
  }
  return <RequestFailureAlert failure={failure} />
}

/**
 * Ação de jogo recusada com 404: a sala já não aceita escolhas (`Expired` por qualquer motivo). O aviso diz só o que
 * aconteceu com a escolha, sem afirmar o motivo; a releitura mostra o estado da sala.
 */
function ChoiceFailure({ error }: { readonly error: Error | null }): React.JSX.Element {
  const { t } = useI18n()
  const failure = classifyRequestFailure(error)
  if (failure.kind === 'problem' && failure.status === NOT_FOUND) {
    return <p role="status">{t('participation.choiceNotSaved')}</p>
  }
  return <RequestFailureAlert failure={failure} />
}

interface SettledMutation {
  readonly isError: boolean
  readonly submittedAt: number
}

/**
 * A falha de uma mutação descreve a sala em que apareceu. Quando o estado da sala muda de novo, as falhas já assentadas
 * deixam de valer. A mutação ainda pendente fica de fora: é a releitura dela que muda o estado.
 */
function useFailuresOfCurrentState(
  state: string,
  mutations: readonly SettledMutation[],
): (mutation: SettledMutation) => boolean {
  const [seen, setSeen] = useState({ state, staleUntil: 0 })
  if (seen.state !== state) {
    const settled = mutations.filter((mutation) => mutation.isError).map((mutation) => mutation.submittedAt)
    setSeen({ state, staleUntil: Math.max(seen.staleUntil, ...settled) })
  }
  return (mutation) => mutation.isError && mutation.submittedAt > seen.staleUntil
}

function RoomBody({
  room,
  refresh,
}: {
  readonly room: Room
  readonly refresh: () => Promise<void>
}): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  const command = usePrivateMutation({
    mutationFn: (operation, action: 'accept' | 'reject' | 'exit') => roomCommand(operation, room.roomId, action),
    onSuccess: refresh,
    onError: refresh,
  })
  const action = usePrivateMutation({
    mutationFn: (operation, choice: { type: 'choose-option' | 'skip-card'; cardId: string; optionId?: string }) =>
      act(operation, room, choice),
    onSuccess: refresh,
    onError: refresh,
  })
  const routesAction = usePrivateMutation({
    mutationFn: (operation, choice: RoutesAction) => actOnRoutes(operation, room, choice),
    onSuccess: refresh,
    onError: refresh,
  })
  const failedNow = useFailuresOfCurrentState(room.state, [action, routesAction])
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
      {command.isError ? <RoomFailure failure={classifyRequestFailure(command.error)} /> : null}
      {failedNow(action) ? <ChoiceFailure error={action.error} /> : null}
      {failedNow(routesAction) ? <ChoiceFailure error={routesAction.error} /> : null}
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
        <section
          key={audio.cueId}
          lang={room.displayLocale}
          aria-label={audio.accessibleDescription}
          className="flex flex-col gap-2"
        >
          <p>{audio.accessibleDescription}</p>
          <PrivateAudioPlayer path={audio.url} />
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

/**
 * Participante que deixou de ser elegível não lê mais a sala, mas ainda pode sair dela (API-R04, ADR-0052). Conta
 * privilegiada também recebe `FORBIDDEN` ao ler, e a rota de saída continua recusando-a.
 */
function ExitAfterForbiddenRead({
  roomId,
  failure,
  onExited,
}: {
  readonly roomId: string
  readonly failure: ReturnType<typeof classifyRequestFailure>
  readonly onExited: () => void
}): React.JSX.Element | null {
  const { t } = useI18n()
  const { roles } = useAccount()
  const exit = usePrivateMutation({
    mutationFn: (operation) => roomCommand(operation, roomId, 'exit'),
    onSuccess: onExited,
  })
  const privileged = roles.some((role) => role !== 'participant')
  if (privileged || failure.kind !== 'problem' || failure.code !== 'FORBIDDEN') {
    return null
  }
  return (
    <>
      {exit.isError ? <RequestFailureAlert failure={classifyRequestFailure(exit.error)} /> : null}
      <Button
        variant="secondary"
        disabled={exit.isPending}
        onClick={() => {
          exit.mutate()
        }}
      >
        {t('participation.exit')}
      </Button>
    </>
  )
}
