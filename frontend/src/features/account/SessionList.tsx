import { useId } from 'react'
import type { AccountSession } from '@/features/session/session-api'
import { useRevokeSession, useSessions } from '@/features/session/session-hooks'
import { classifyRequestFailure } from '@/shared/api'
import { messages } from '@/shared/messages'
import { RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'

const dateTimeFormat = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' })

interface SessionListProps {
  readonly userId: string
}

/** Sessões da conta (API-A09/A10). Carregando, erro, vazio e lista são estados distintos (spec 13.2). */
export function SessionList({ userId }: SessionListProps): React.JSX.Element {
  const headingId = useId()
  const sessions = useSessions(userId)
  const revoke = useRevokeSession(userId)

  return (
    <section aria-labelledby={headingId} className="flex flex-col gap-4">
      <div>
        <h2 id={headingId} className="text-xl font-semibold">
          {messages.sessions.title}
        </h2>
        <p className="mt-1 text-text-muted">{messages.sessions.intro}</p>
      </div>
      {revoke.isError ? <RequestFailureAlert failure={classifyRequestFailure(revoke.error)} /> : null}
      <SessionListBody
        sessions={sessions.data}
        failure={sessions.isError ? sessions.error : null}
        revokingId={revoke.isPending ? revoke.variables.id : null}
        onRetry={() => {
          void sessions.refetch()
        }}
        onRevoke={(session) => {
          if (!revoke.isPending) {
            revoke.mutate(session)
          }
        }}
      />
    </section>
  )
}

interface SessionListBodyProps {
  readonly sessions: readonly AccountSession[] | undefined
  readonly failure: Error | null
  readonly revokingId: string | null
  readonly onRetry: () => void
  readonly onRevoke: (session: AccountSession) => void
}

function SessionListBody({ sessions, failure, revokingId, onRetry, onRevoke }: SessionListBodyProps) {
  if (sessions === undefined && failure !== null) {
    return (
      <div className="flex flex-col items-start gap-4">
        <p className="text-text-muted">{messages.sessions.loadError}</p>
        <RequestFailureAlert failure={classifyRequestFailure(failure)} />
        <Button variant="secondary" onClick={onRetry}>
          {messages.common.retry}
        </Button>
      </div>
    )
  }
  if (sessions === undefined) {
    return (
      <p role="status" className="text-text-muted">
        {messages.sessions.loading}
      </p>
    )
  }
  if (sessions.length === 0) {
    return <p className="text-text-muted">{messages.sessions.empty}</p>
  }
  return (
    <ul className="flex flex-col gap-3">
      {sessions.map((session) => (
        <SessionItem
          key={session.id}
          session={session}
          revoking={revokingId === session.id}
          onRevoke={() => {
            onRevoke(session)
          }}
        />
      ))}
    </ul>
  )
}

interface SessionItemProps {
  readonly session: AccountSession
  readonly revoking: boolean
  readonly onRevoke: () => void
}

function SessionItem({ session, revoking, onRevoke }: SessionItemProps): React.JSX.Element {
  const revokeLabel = session.current ? messages.sessions.revokeCurrent : messages.sessions.revoke
  return (
    <li className="flex flex-col gap-3 rounded-md border border-border p-4 sm:flex-row sm:items-center sm:justify-between">
      <div className="flex flex-col gap-1">
        <p className="font-semibold break-words">
          {session.description}
          {session.current ? (
            <span className="ml-2 rounded-md bg-surface-muted px-2 py-1 text-sm font-semibold text-text">
              {messages.sessions.current}
            </span>
          ) : null}
        </p>
        <p className="text-sm text-text-muted">
          {messages.sessions.createdAt}: <time dateTime={session.createdAt}>{formatInstant(session.createdAt)}</time>
          {' · '}
          {messages.sessions.lastSeenAt}: <time dateTime={session.lastSeenAt}>{formatInstant(session.lastSeenAt)}</time>
        </p>
      </div>
      <Button
        variant="secondary"
        aria-disabled={revoking}
        aria-label={`${revoking ? messages.sessions.revoking : revokeLabel}: ${session.description}`}
        onClick={onRevoke}
      >
        {revoking ? messages.sessions.revoking : revokeLabel}
      </Button>
    </li>
  )
}

function formatInstant(instant: string): string {
  return dateTimeFormat.format(new Date(instant))
}
