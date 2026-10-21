import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useRef, useState } from 'react'
import { useAccount } from '@/features/session/account-context'
import type { Me } from '@/features/session/session-api'
import { meQueryKey } from '@/features/session/query-keys'
import { classifyRequestFailure, isUnauthorized } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { CheckboxField, SelectField } from '@/shared/ui/form-fields'
import { RegionSuggestion } from './RegionSuggestion'
import { fetchRegionalPreferences, saveRegionalPreferences, type RegionalPreferences } from './regional-preferences-api'

export function RegionalPreferenceSection(): React.JSX.Element {
  const { id } = useAccount()
  const { t } = useI18n()
  const query = useQuery({
    queryKey: ['account', id, 'regional-preferences'],
    queryFn: ({ signal }) => fetchRegionalPreferences(signal),
    gcTime: 0,
  })
  return query.isError ? (
    <RequestFailureAlert failure={classifyRequestFailure(query.error)} />
  ) : query.data === undefined ? (
    <p role="status">{t('regional.loading')}</p>
  ) : (
    <RegionalPreferenceForm key={id} initial={query.data} />
  )
}

function RegionalPreferenceForm({ initial }: { readonly initial: RegionalPreferences }): React.JSX.Element {
  const { t } = useI18n()
  const account = useAccount()
  const queries = useQueryClient()
  const [base, setBase] = useState(initial)
  const manuallyEdited = useRef(false)
  const [mode, setMode] = useState(initial.mode)
  const [brazil, setBrazil] = useState(initial.countryCodes.includes('BR'))
  const [invalid, setInvalid] = useState(false)
  const [reloaded, setReloaded] = useState(false)
  const save = useMutation({
    mutationFn: () => {
      if (mode === 'pending' || (mode === 'countries' && !brazil)) throw new Error('Explicit regional choice required')
      return saveRegionalPreferences(base.version, mode, mode === 'countries' ? ['BR'] : [])
    },
    onSuccess: (value) => {
      if (queries.getQueryData<Me>(meQueryKey)?.id !== account.id) return
      setBase(value)
      setReloaded(false)
    },
    onError: (error) => {
      if (isUnauthorized(error)) void queries.invalidateQueries({ queryKey: meQueryKey })
    },
  })
  const reload = useMutation({
    mutationFn: () => fetchRegionalPreferences(),
    onSuccess: (value) => {
      setBase(value)
      setReloaded(true)
      save.reset()
    },
  })
  const failure = save.isError ? classifyRequestFailure(save.error) : null
  const conflict = failure?.kind === 'problem' && failure.status === 412
  const busy = save.isPending || reload.isPending
  const edited = () => {
    manuallyEdited.current = true
    setInvalid(false)
    setReloaded(false)
    if (save.isSuccess) save.reset()
  }
  return (
    <form
      className="flex flex-col gap-4 border-t border-border pt-6"
      onSubmit={(event) => {
        event.preventDefault()
        if (mode === 'pending' || (mode === 'countries' && !brazil)) {
          setInvalid(true)
          return
        }
        if (!busy && !conflict) {
          manuallyEdited.current = true
          save.mutate()
        }
      }}
    >
      <Alert variant="info">{t('regional.activation')}</Alert>
      {base.mode === 'pending' ? <Alert variant="info">{t('regional.pending')}</Alert> : null}
      <SelectField
        label={t('regional.label')}
        hint={t('regional.hint')}
        error={invalid ? t('regional.invalid') : undefined}
        value={mode}
        disabled={busy}
        onChange={(event) => {
          const value = event.target.value
          if (value === 'countries' || value === 'unrestricted') setMode(value)
          edited()
        }}
        options={[
          { value: 'pending', label: t('regional.choose') },
          { value: 'countries', label: t('regional.countries') },
          { value: 'unrestricted', label: t('regional.unrestricted') },
        ]}
      />
      {mode === 'countries' ? (
        <fieldset disabled={busy}>
          <legend className="font-semibold">{t('regional.acceptedCountries')}</legend>
          <CheckboxField
            label={t('regional.brazil')}
            checked={brazil}
            onChange={(event) => {
              setBrazil(event.target.checked)
              edited()
            }}
          />
        </fieldset>
      ) : null}
      <RegionSuggestion
        onCountry={() => {
          if (manuallyEdited.current || base.mode !== 'pending' || busy || conflict) return false
          setMode('countries')
          setBrazil(true)
          setInvalid(false)
          return true
        }}
      />
      {conflict ? (
        <>
          <Alert variant="error">{t('regional.conflict')}</Alert>
          <Button
            type="button"
            disabled={busy}
            onClick={() => {
              manuallyEdited.current = true
              reload.mutate()
            }}
          >
            {t('regional.reload')}
          </Button>
        </>
      ) : failure === null ? null : (
        <RequestFailureAlert failure={failure} />
      )}
      {reload.isError ? <RequestFailureAlert failure={classifyRequestFailure(reload.error)} /> : null}
      {reloaded ? (
        <Alert variant="info">
          {t('regional.reloaded', {
            choice: t(
              base.mode === 'countries'
                ? 'regional.brazil'
                : base.mode === 'unrestricted'
                  ? 'regional.unrestricted'
                  : 'regional.choose',
            ),
          })}
        </Alert>
      ) : null}
      {save.isSuccess ? <Alert variant="success">{t('regional.saved')}</Alert> : null}
      <Button type="submit" disabled={busy || conflict}>
        {t('regional.save')}
      </Button>
    </form>
  )
}
