import { usePrivateOperation } from '@/features/session/private-operation'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useId, useMemo, useState } from 'react'
import { useAccount } from '@/features/session/account-context'
import { meQueryKey } from '@/features/session/query-keys'
import type { Me } from '@/features/session/session-api'
import { classifyRequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { TextField } from '@/shared/ui/form-fields'
import { fetchTimeZone, fetchTimeZones, saveTimeZone, type AccountTimeZone } from './time-zone-api'

export function AccountTimeZoneSection(): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { id } = useAccount()
  const { t } = useI18n()
  const query = useQuery({
    queryKey: ['account', id, 'time-zone'],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchTimeZone(operation, signal)),
    gcTime: 0,
  })
  const options = useQuery({
    queryKey: ['time-zone-options'],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchTimeZones(operation, signal)),
  })
  if (query.isError || options.isError)
    return <RequestFailureAlert failure={classifyRequestFailure(query.error ?? options.error)} />
  if (query.data === undefined || options.data === undefined) return <p role="status">{t('timeZone.loading')}</p>
  return <AccountTimeZoneForm initial={query.data} zones={options.data} />
}

function canonicalZone(zone: string): string | null {
  try {
    return new Intl.DateTimeFormat('en-US', { timeZone: zone }).resolvedOptions().timeZone
  } catch (error) {
    if (error instanceof RangeError) return null
    throw error
  }
}

function zoneLabel(zone: string, locale: string): string {
  if (zone === 'UTC') return 'UTC'
  try {
    const name = new Intl.DateTimeFormat(locale, { timeZone: zone, timeZoneName: 'longGeneric' })
      .formatToParts(new Date(0))
      .find((part) => part.type === 'timeZoneName')?.value
    return `${name ?? zone.replaceAll('_', ' ')} — ${zone}`
  } catch (error) {
    if (error instanceof RangeError) return zone.replaceAll('_', ' ')
    throw error
  }
}

function AccountTimeZoneForm({
  initial,
  zones,
}: {
  readonly initial: AccountTimeZone
  readonly zones: readonly string[]
}): React.JSX.Element {
  const { t, locale } = useI18n()
  const account = useAccount()
  const queries = useQueryClient()
  const listId = useId()
  const [base, setBase] = useState(initial)
  const [draft, setDraft] = useState(initial.timeZone ?? '')
  const [invalid, setInvalid] = useState(false)
  const [reloaded, setReloaded] = useState(false)
  const options = useMemo(
    () => zones.map((zone) => ({ zone, label: zoneLabel(zone, locale), canonical: canonicalZone(zone) })),
    [zones, locale],
  )
  const canonical = draft.trim() === '' ? null : canonicalZone(draft.trim())
  const selected =
    options.find((option) => option.zone === draft.trim() || option.label === draft.trim())?.zone ??
    options.find((option) => canonical !== null && option.canonical === canonical)?.zone ??
    null
  const save = usePrivateMutation({
    mutationFn: (operation) => saveTimeZone(operation, base.version, selected),
    onSuccess: (value) => {
      if (queries.getQueryData<Me>(meQueryKey)?.id !== account.id) return
      setBase(value)
      setDraft(value.timeZone ?? '')
      setReloaded(false)
      queries.setQueryData<Me>(meQueryKey, (current) =>
        current === undefined ? current : { ...current, preferredTimeZone: value.timeZone },
      )
    },
  })
  const reload = usePrivateMutation({
    mutationFn: (operation) => fetchTimeZone(operation),
    onSuccess: (value) => {
      setBase(value)
      setReloaded(true)
      save.reset()
    },
  })
  const failure = save.isError ? classifyRequestFailure(save.error) : null
  const conflict = failure?.kind === 'problem' && failure.status === 412
  return (
    <form
      className="flex flex-col gap-4 border-t border-border pt-6"
      onSubmit={(event) => {
        event.preventDefault()
        if (draft.trim() !== '' && selected === null) {
          setInvalid(true)
          return
        }
        if (!save.isPending && !conflict && !reload.isPending) save.mutate()
      }}
    >
      <TextField
        label={t('timeZone.label')}
        hint={t('timeZone.hint')}
        error={invalid ? t('timeZone.invalid') : undefined}
        list={listId}
        value={draft}
        disabled={save.isPending || reload.isPending}
        autoComplete="off"
        onChange={(event) => {
          const value = event.target.value
          // Native datalist suggestions have translated labels. Keep the ID so changing locale preserves the draft.
          setDraft(options.find((option) => option.label === value)?.zone ?? value)
          setInvalid(false)
          if (save.isSuccess) save.reset()
        }}
      />
      <datalist id={listId}>
        {options.map((option) => (
          <option key={option.zone} value={option.label} />
        ))}
      </datalist>
      <Button
        type="button"
        variant="secondary"
        disabled={save.isPending || reload.isPending}
        onClick={() => {
          setDraft(new Intl.DateTimeFormat().resolvedOptions().timeZone)
          setInvalid(false)
          if (save.isSuccess) save.reset()
        }}
      >
        {t('timeZone.device')}
      </Button>
      {conflict ? (
        <>
          <Alert variant="error">{t('timeZone.conflict')}</Alert>
          <Button
            type="button"
            disabled={reload.isPending}
            onClick={() => {
              reload.mutate()
            }}
          >
            {t('timeZone.reload')}
          </Button>
        </>
      ) : failure === null ? null : (
        <RequestFailureAlert failure={failure} />
      )}
      {reload.isError ? <RequestFailureAlert failure={classifyRequestFailure(reload.error)} /> : null}
      {reloaded ? (
        <Alert variant="info">{t('timeZone.reloaded', { zone: base.timeZone ?? t('timeZone.unset') })}</Alert>
      ) : null}
      {save.isSuccess ? <Alert variant="success">{t('timeZone.saved')}</Alert> : null}
      <Button type="submit" disabled={save.isPending || reload.isPending || conflict}>
        {t('timeZone.save')}
      </Button>
    </form>
  )
}
