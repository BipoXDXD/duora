import { useState } from 'react'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Button } from '@/shared/ui/button'
import type { RoutesAction, RoutesGame } from './participation-api'

export function RoutesGamePanel({
  game,
  locale,
  active,
  busy,
  onAction,
}: {
  readonly game: RoutesGame
  readonly locale: string
  readonly active: boolean
  readonly busy: boolean
  readonly onAction: (action: RoutesAction) => void
}): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  const can = (type: RoutesAction['type']) =>
    active && game.status === 'Active' && !busy && game.availableActions.includes(type)
  const stage = game.stage
  return (
    <section lang={locale} className="flex flex-col gap-4">
      <h2 className="text-xl font-semibold">{game.title}</h2>
      <p>{game.objective}</p>
      <p>{t('routes.deadline', { deadline: formatDateTime(new Date(game.deadline)) })}</p>
      <h3 className="font-semibold">{t(`routes.stage.${stage}`)}</h3>
      {stage === 'Introduction' ? (
        <p>{game.example}</p>
      ) : (
        <section aria-label={t('routes.ownClues')}>
          <h4 className="font-semibold">{t('routes.ownClues')}</h4>
          <ul className="list-disc pl-6">
            {game.myClues.map((clue, index) => (
              <li key={index}>{clue}</li>
            ))}
          </ul>
        </section>
      )}
      {active && game.awaitingPartner && stage !== 'Choices' ? (
        <p role="status">{t('routes.awaitingPartner')}</p>
      ) : null}
      {active && (stage === 'Introduction' || stage === 'Clues') ? (
        <Button
          disabled={!can('acknowledge')}
          onClick={() => {
            onAction({ type: 'acknowledge', stage })
          }}
        >
          {t('routes.acknowledge')}
        </Button>
      ) : null}
      {active && stage === 'Choices' && game.currentStop !== null ? (
        <>
          <p role="status">{t(game.isMyTurn ? 'routes.yourTurn' : 'routes.partnerTurn')}</p>
          <RouteChoice
            key={`${game.resourceVersion}:${game.currentStop.stepId}`}
            stop={game.currentStop}
            disabled={!can('choose-option') || !game.isMyTurn}
            onAction={onAction}
          />
        </>
      ) : null}
      {game.route.length > 0 ? (
        <section aria-label={t('routes.route')}>
          <h4 className="font-semibold">{t('routes.route')}</h4>
          <ol className="list-decimal pl-6">
            {game.route.map((stop) => (
              <li key={stop.stepId}>
                <span className="font-semibold">{stop.prompt}</span>
                <p>{stop.optionLabel}</p>
              </li>
            ))}
          </ol>
        </section>
      ) : null}
      {active && stage === 'Adjustment' ? (
        <>
          <p>{t('routes.revisionsRemaining', { count: game.revisionsRemaining })}</p>
          <Button
            disabled={!can('confirm-route')}
            onClick={() => {
              onAction({ type: 'confirm-route' })
            }}
          >
            {t('routes.confirm')}
          </Button>
          <p>{t('routes.revisionExplanation')}</p>
          <Button
            variant="secondary"
            disabled={!can('request-revision') || game.revisionsRemaining <= 0}
            onClick={() => {
              onAction({ type: 'request-revision' })
            }}
          >
            {t('routes.revise')}
          </Button>
        </>
      ) : null}
      {stage === 'Conclusion' ? <p>{t('routes.conclusion')}</p> : null}
    </section>
  )
}
function RouteChoice({
  stop,
  disabled,
  onAction,
}: {
  readonly stop: NonNullable<RoutesGame['currentStop']>
  readonly disabled: boolean
  readonly onAction: (action: RoutesAction) => void
}): React.JSX.Element {
  const { t } = useI18n()
  const [optionId, setOptionId] = useState('')
  return (
    <fieldset disabled={disabled} className="flex flex-col gap-3">
      <legend className="mb-3 font-semibold">{stop.prompt}</legend>
      {stop.options.map((option) => (
        <label key={option.optionId} className="flex min-h-11 items-center gap-3">
          <input
            type="radio"
            name={`route-${stop.stepId}`}
            value={option.optionId}
            checked={optionId === option.optionId}
            onChange={() => {
              setOptionId(option.optionId)
            }}
          />
          {option.label}
        </label>
      ))}
      <Button
        disabled={disabled || optionId === ''}
        onClick={() => {
          onAction({ type: 'choose-option', stepId: stop.stepId, optionId })
        }}
      >
        {t('routes.choose')}
      </Button>
    </fieldset>
  )
}
