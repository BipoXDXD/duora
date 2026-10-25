import { usePrivateOperation } from '@/features/session/private-operation'
import { usePrivateMutation } from '@/features/session/private-mutation'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useAccount } from '@/features/session/account-context'
import type { Me } from '@/features/session/session-api'
import { meQueryKey } from '@/features/session/query-keys'
import { classifyRequestFailure, isUnauthorized } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { CheckboxField, SelectField } from '@/shared/ui/form-fields'
import { Page, TextLink } from '@/shared/ui/page'
import { RegionalPreferenceSection } from './RegionalPreferenceForm'
import { AccountTimeZoneSection } from './AccountTimeZoneForm'
import {
  fetchLocale,
  fetchPreferences,
  saveLocale,
  savePreferences,
  type AccountLocale,
  type CommunicationPreferences,
  type Language,
} from './preferences-api'

export function PreferencesPage(): React.JSX.Element {
  const privateOperation = usePrivateOperation()
  const { id } = useAccount()
  const { t } = useI18n()
  const prefs = useQuery({
    queryKey: ['account', id, 'preferences'],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchPreferences(operation, signal)),
    gcTime: 0,
  })
  const locale = useQuery({
    queryKey: ['account', id, 'locale'],
    queryFn: ({ signal }) => privateOperation.run((operation) => fetchLocale(operation, signal)),
    gcTime: 0,
  })
  return (
    <Page title={t('preferences.title')} intro={t('preferences.intro')}>
      <p>{t('preferences.noTranslation')}</p>
      <p>{t('preferences.voiceOptional')}</p>
      {prefs.isError ? (
        <RequestFailureAlert failure={classifyRequestFailure(prefs.error)} />
      ) : prefs.data === undefined ? (
        <p role="status">{t('preferences.loading')}</p>
      ) : (
        <PreferenceForm initial={prefs.data} />
      )}
      {locale.isError ? (
        <RequestFailureAlert failure={classifyRequestFailure(locale.error)} />
      ) : locale.data === undefined ? null : (
        <LocaleForm initial={locale.data} />
      )}
      <RegionalPreferenceSection />
      <AccountTimeZoneSection />
      <TextLink to={paths.account}>{t('profile.backToAccount')}</TextLink>
    </Page>
  )
}
function PreferenceForm({ initial }: { readonly initial: CommunicationPreferences }): React.JSX.Element {
  const { t } = useI18n()
  const queries = useQueryClient()
  const [base, setBase] = useState(initial)
  const [languages, setLanguages] = useState(initial.communicationLanguages)
  const [optIn, setOptIn] = useState(initial.allowDifferentDisplayLocales)
  const [invalid, setInvalid] = useState(false)
  const [reloaded, setReloaded] = useState(false)
  const save = usePrivateMutation({
    mutationFn: (operation) =>
      savePreferences(operation, base.version, {
        communicationLanguages: languages,
        allowDifferentDisplayLocales: optIn,
      }),
    onSuccess: (value) => {
      setBase(value)
      setReloaded(false)
    },
    onError: (error) => {
      if (isUnauthorized(error)) void queries.invalidateQueries({ queryKey: meQueryKey })
    },
  })
  const reload = usePrivateMutation({
    mutationFn: (operation) => fetchPreferences(operation),
    onSuccess: (value) => {
      setBase(value)
      setReloaded(true)
      save.reset()
    },
  })
  const failure = save.isError ? classifyRequestFailure(save.error) : null
  const conflict = failure?.kind === 'problem' && failure.status === 412
  const toggle = (language: Language, checked: boolean) => {
    setLanguages((current) => (checked ? [...current, language].sort() : current.filter((item) => item !== language)))
    setInvalid(false)
    save.reset()
  }
  return (
    <form
      className="flex flex-col gap-4"
      onSubmit={(event) => {
        event.preventDefault()
        if (languages.length === 0) {
          setInvalid(true)
          return
        }
        if (!save.isPending && !conflict) save.mutate()
      }}
    >
      {base.declarationRequired ? <Alert variant="info">{t('preferences.declarationRequired')}</Alert> : null}
      <fieldset disabled={save.isPending || reload.isPending}>
        <legend className="font-semibold">{t('preferences.languages')}</legend>
        <CheckboxField
          label={t('preferences.english')}
          checked={languages.includes('en')}
          onChange={(event) => {
            toggle('en', event.target.checked)
          }}
        />
        <CheckboxField
          label={t('preferences.portuguese')}
          checked={languages.includes('pt')}
          onChange={(event) => {
            toggle('pt', event.target.checked)
          }}
        />
        <CheckboxField
          label={t('preferences.optIn')}
          checked={optIn}
          onChange={(event) => {
            setOptIn(event.target.checked)
          }}
        />
      </fieldset>
      {invalid ? <Alert variant="error">{t('preferences.required')}</Alert> : null}
      {conflict ? (
        <>
          <Alert variant="error">{t('preferences.conflict')}</Alert>
          <Button
            type="button"
            disabled={reload.isPending}
            onClick={() => {
              reload.mutate()
            }}
          >
            {t('preferences.reload')}
          </Button>
        </>
      ) : failure === null ? null : (
        <RequestFailureAlert failure={failure} />
      )}
      {reload.isError ? <RequestFailureAlert failure={classifyRequestFailure(reload.error)} /> : null}
      {reloaded ? (
        <Alert variant="info">
          <p>{t('preferences.reloaded')}</p>
          <p>
            {t('preferences.savedLanguages')}:{' '}
            {base.communicationLanguages
              .map((language) => t(language === 'en' ? 'preferences.english' : 'preferences.portuguese'))
              .join(', ')}
          </p>
          <p>{t(base.allowDifferentDisplayLocales ? 'preferences.optInYes' : 'preferences.optInNo')}</p>
        </Alert>
      ) : null}
      {save.isSuccess ? (
        <Alert variant="success" focusOnMount>
          {t('preferences.saved')}
        </Alert>
      ) : null}
      <Button type="submit" disabled={save.isPending || conflict}>
        {t('preferences.save')}
      </Button>
    </form>
  )
}
function LocaleForm({ initial }: { readonly initial: AccountLocale }): React.JSX.Element {
  const { t, changeLocale } = useI18n()
  const account = useAccount()
  const queries = useQueryClient()
  const [base, setBase] = useState(initial)
  const [locale, setLocale] = useState(initial.locale)
  const save = usePrivateMutation({
    mutationFn: (operation) => saveLocale(operation, base.version, locale),
    onSuccess: (value) => {
      if (queries.getQueryData<Me>(meQueryKey)?.id !== account.id) return
      setBase(value)
      queries.setQueryData<Me>(meQueryKey, (current) =>
        current === undefined ? current : { ...current, preferredLocale: value.locale },
      )
      changeLocale(value.locale)
    },
  })
  const reload = usePrivateMutation({
    mutationFn: (operation) => fetchLocale(operation),
    onSuccess: (value) => {
      setBase(value)
      setLocale(value.locale)
      save.reset()
    },
  })
  return (
    <form
      className="flex flex-col gap-4 border-t border-border pt-6"
      onSubmit={(event) => {
        event.preventDefault()
        if (!save.isPending) save.mutate()
      }}
    >
      <SelectField
        label={t('preferences.displayLocale')}
        hint={t('preferences.localeIndependent')}
        value={locale}
        onChange={(event) => {
          if (event.target.value === 'pt-BR' || event.target.value === 'en-US') setLocale(event.target.value)
        }}
        options={[
          { value: 'en-US', label: t('preferences.english') },
          { value: 'pt-BR', label: t('preferences.portuguese') },
        ]}
      />
      {save.isError ? (
        <>
          <RequestFailureAlert failure={classifyRequestFailure(save.error)} />
          <Button
            type="button"
            onClick={() => {
              reload.mutate()
            }}
          >
            {t('preferences.reload')}
          </Button>
        </>
      ) : null}
      {reload.isError ? <RequestFailureAlert failure={classifyRequestFailure(reload.error)} /> : null}
      {save.isSuccess ? <Alert variant="success">{t('preferences.localeSaved')}</Alert> : null}
      <Button type="submit" disabled={save.isPending}>
        {t('preferences.saveLocale')}
      </Button>
    </form>
  )
}
