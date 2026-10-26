import { usePrivateOperation } from '@/features/session/private-operation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useParams } from 'react-router'
import { SignInAgainButton } from '@/features/auth/SignInAgainButton'
import { useAccount } from '@/features/session/account-context'
import { meQueryKey } from '@/features/session/query-keys'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { Page, TextLink } from '@/shared/ui/page'
import {
  fetchReviewCapabilities,
  fetchReviewItem,
  fetchReviewPage,
  reviewPath,
  sendReviewIntent,
  type ReviewCapabilities,
  type ReviewItem,
  type ReviewKind,
  type ReviewPage,
} from './photo-review-api'
import { needsReviewRevalidation, reviewHttpStatus, useReviewCommand } from './review-command'
import { ReviewEvidence } from './ReviewEvidence'
import { ReviewFailure, ReviewStateLabel } from './ReviewFeedback'
import { ReviewAssignmentForm, ReviewDecisionForm, ReviewerReauthentication } from './ReviewForms'

function canRead(kind: ReviewKind, capabilities: ReviewCapabilities): boolean {
  return kind === 'appeal' ? capabilities.appeal : capabilities.assign || capabilities.evidence || capabilities.decide
}

export function AdminPhotoReviewsPage({ kind }: { readonly kind: ReviewKind }): React.JSX.Element {
  const account = useAccount()
  const params = useParams<{ caseId: string; appealId: string }>()
  const itemId = kind === 'review' ? params.caseId : params.appealId
  return (
    <ReviewWorkspace
      key={`${account.id}:${kind}:${itemId ?? 'queue'}`}
      kind={kind}
      userId={account.id}
      itemId={itemId}
    />
  )
}

export function PhotoReviewsPage(): React.JSX.Element {
  return <AdminPhotoReviewsPage kind="review" />
}

export function PhotoAppealsPage(): React.JSX.Element {
  return <AdminPhotoReviewsPage kind="appeal" />
}

function ReviewWorkspace({
  kind,
  userId,
  itemId,
}: {
  readonly kind: ReviewKind
  readonly userId: string
  readonly itemId: string | undefined
}): React.JSX.Element {
  const { t } = useI18n()
  const client = useQueryClient()
  const privateOperation = usePrivateOperation()
  const [cursor, setCursor] = useState<string | null>(null)
  const [epoch, setEpoch] = useState(0)
  const [stopped, setStopped] = useState(false)
  const [evidenceEpoch, setEvidenceEpoch] = useState(0)
  const capabilities = useQuery({
    queryKey: ['photo-review', userId, kind, 'capabilities', epoch],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchReviewCapabilities(operation, signal)),
    gcTime: 0,
    retry: false,
  })
  const allowed = capabilities.isSuccess && canRead(kind, capabilities.data)
  const read = useQuery<ReviewPage | ReviewItem>({
    queryKey: ['photo-review', userId, kind, itemId ?? 'queue', cursor, epoch],
    queryFn: ({ signal }) =>
      itemId === undefined
        ? privateOperation.run((operation) => fetchReviewPage(operation, { kind, cursor }, signal))
        : privateOperation.run((operation) => fetchReviewItem(operation, { kind, id: itemId }, signal)),
    enabled: allowed && !stopped,
    gcTime: 0,
    retry: false,
  })
  const refreshCapabilities = capabilities.refetch
  useEffect(() => {
    if (read.isError && needsReviewRevalidation(read.error)) void refreshCapabilities()
  }, [read.isError, read.error, refreshCapabilities])
  useEffect(() => {
    if (reviewHttpStatus(capabilities.error ?? read.error) === 401)
      void client.invalidateQueries({ queryKey: meQueryKey })
  }, [capabilities.error, read.error, client])

  function revalidate(error: unknown): void {
    if (reviewHttpStatus(error) === 401) void client.invalidateQueries({ queryKey: meQueryKey })
    if (needsReviewRevalidation(error)) {
      setEvidenceEpoch((value) => value + 1)
      setStopped(true)
      void capabilities.refetch().then((result) => {
        if (result.isSuccess && canRead(kind, result.data)) void read.refetch()
      })
    }
  }

  const command = useReviewCommand({
    send: sendReviewIntent,
    onConfirmed: () => {
      setStopped(true)
      setEvidenceEpoch((value) => value + 1)
      void capabilities.refetch()
      void read.refetch()
    },
    onFailure: revalidate,
  })

  function reload(): void {
    command.reset()
    setStopped(false)
    setEpoch((value) => value + 1)
  }

  const failure = capabilities.isError ? capabilities.error : read.isError ? read.error : null
  const data = allowed && !stopped && !read.isError ? read.data : undefined
  return (
    <Page
      width="wide"
      title={t(kind === 'review' ? 'photoReview.reviewsTitle' : 'photoReview.appealsTitle')}
      intro={t('photoReview.adminIntro')}
    >
      {itemId === undefined ? null : <TextLink to={reviewPath(kind)}>{t('photoReview.backQueue')}</TextLink>}
      <nav aria-label={t('photoReview.queueNavigation')} className="flex flex-wrap gap-4">
        <TextLink to={reviewPath('review')}>{t('photoReview.reviewsTitle')}</TextLink>
        {capabilities.isSuccess && capabilities.data.appeal ? (
          <TextLink to={reviewPath('appeal')}>{t('photoReview.appealsTitle')}</TextLink>
        ) : null}
      </nav>
      {failure === null ? null : <ReviewFailure error={failure} />}
      {command.error === null ? null : <ReviewFailure error={command.error} focusOnMount />}
      {command.uncertain ? (
        <Button aria-disabled={command.busy} onClick={command.retry}>
          {command.busy ? t('photoReview.sending') : t('photoReview.retrySame')}
        </Button>
      ) : null}
      {command.confirmed ? (
        <Alert variant="success" focusOnMount>
          {t(
            command.intent?.command.type === 'assignment'
              ? 'photoReview.assignmentConfirmed'
              : 'photoReview.decisionConfirmed',
          )}
        </Alert>
      ) : null}
      {reviewHttpStatus(command.error ?? failure) === 401 ? (
        <>
          <ReviewerReauthentication onConfirmed={reload} />
          <SignInAgainButton />
        </>
      ) : null}
      {capabilities.isPending || (allowed && read.isPending && !stopped) ? (
        <p role="status">{t('photoReview.loading')}</p>
      ) : null}
      {capabilities.isSuccess && !allowed ? <Alert variant="error">{t('photoReview.denied')}</Alert> : null}
      {failure !== null || command.error !== null || stopped || (capabilities.isSuccess && !allowed) ? (
        <Button variant="secondary" onClick={reload}>
          {t('photoReview.readCurrent')}
        </Button>
      ) : null}
      {data === undefined ? null : 'items' in data ? (
        <>
          {data.items.length === 0 ? (
            <p>{t('photoReview.empty')}</p>
          ) : (
            <ul className="flex flex-col gap-6">
              {data.items.map((item) => (
                <li key={item.id} className="rounded-md border border-border p-4">
                  <TextLink to={`${reviewPath(kind)}/${encodeURIComponent(item.id)}`}>
                    {t('photoReview.openCase', { id: item.id })}
                  </TextLink>
                  <ReviewMetadata item={item} />
                </li>
              ))}
            </ul>
          )}
          {data.nextCursor === null ? null : (
            <Button
              aria-disabled={read.isFetching}
              onClick={() => {
                if (!read.isFetching) setCursor(data.nextCursor)
              }}
            >
              {t('photoReview.nextPage')}
            </Button>
          )}
          {cursor === null ? null : (
            <Button
              variant="neutral"
              onClick={() => {
                setCursor(null)
              }}
            >
              {t('photoReview.firstPage')}
            </Button>
          )}
        </>
      ) : (
        <>
          <ReviewMetadata item={data} />
          {data.state !== 'AwaitingReview' && data.state !== 'Open' ? null : (
            <>
              {capabilities.data?.evidence !== true ? null : (
                <ReviewEvidence
                  key={`${String(evidenceEpoch)}:${data.eTag}`}
                  kind={kind}
                  itemId={data.id}
                  onFailure={revalidate}
                />
              )}
              {command.uncertain ? null : (
                <>
                  {capabilities.data?.assign !== true ? null : (
                    <ReviewAssignmentForm
                      busy={command.busy}
                      onAssign={(reviewerId) => {
                        command.submit({
                          kind,
                          id: data.id,
                          eTag: data.eTag,
                          key: crypto.randomUUID(),
                          command: { type: 'assignment', reviewerId },
                        })
                      }}
                    />
                  )}
                  {capabilities.data?.decide !== true ? null : (
                    <>
                      {capabilities.data.recentMfaRequiredForDecision ? (
                        <ReviewerReauthentication
                          onConfirmed={() => {
                            void capabilities.refetch()
                          }}
                        />
                      ) : null}
                      <ReviewDecisionForm
                        busy={command.busy}
                        onDecision={(decision) => {
                          command.submit({
                            kind,
                            id: data.id,
                            eTag: data.eTag,
                            key: crypto.randomUUID(),
                            command: { type: 'decision', decision },
                          })
                        }}
                      />
                    </>
                  )}
                </>
              )}
            </>
          )}
        </>
      )}
    </Page>
  )
}

function ReviewMetadata({ item }: { readonly item: ReviewItem }): React.JSX.Element {
  const { t, formatDateTime } = useI18n()
  return (
    <div className="flex flex-col gap-2">
      <ReviewStateLabel state={item.state} />
      <dl className="grid gap-2 text-sm">
        <div>
          <dt className="font-semibold">{t('photoReview.startedAt')}</dt>
          <dd>{formatDateTime(new Date(item.startedAt))}</dd>
        </div>
        <div>
          <dt className="font-semibold">
            {t(item.retentionUntil === undefined ? 'photoReview.reviewDeadline' : 'photoReview.responseTarget')}
          </dt>
          <dd>{formatDateTime(new Date(item.deadline))}</dd>
        </div>
        {item.retentionUntil === undefined ? null : (
          <div>
            <dt className="font-semibold">{t('photoReview.retentionUntil')}</dt>
            <dd>{formatDateTime(new Date(item.retentionUntil))}</dd>
          </div>
        )}
        {item.policyVersion === undefined ? null : (
          <div>
            <dt className="font-semibold">{t('photoReview.policy')}</dt>
            <dd>{item.policyVersion}</dd>
          </div>
        )}
      </dl>
      <p className="text-sm text-text-muted">{t('photoReview.deadlineHint')}</p>
    </div>
  )
}
