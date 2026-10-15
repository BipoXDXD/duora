import { zodResolver } from '@hookform/resolvers/zod'
import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { useAccount } from '@/features/session/account-context'
import { meQueryKey, profileQueryKey } from '@/features/session/query-keys'
import { classifyRequestFailure, isUnauthorized, type RequestFailure } from '@/shared/api'
import { useI18n } from '@/shared/i18n/i18n-context'
import type { Translate } from '@/shared/i18n/translator'
import { paths } from '@/shared/paths'
import { Alert, RequestFailureAlert } from '@/shared/ui/alert'
import { Button } from '@/shared/ui/button'
import { TextAreaField, TextField } from '@/shared/ui/form-fields'
import { Page, SubmitButton, TextLink } from '@/shared/ui/page'
import { fetchOwnProfile, updateOwnProfile, type OwnProfile } from './profile-api'
import {
  BIO_MAX_LENGTH,
  DISPLAY_NAME_MAX_LENGTH,
  profileFormSchema,
  toProfileChanges,
  type EditedFields,
  type ProfileErrorKey,
  type ProfileFormInput,
  type ProfileFormValues,
} from './profile-form'

const PRECONDITION_FAILED = 412

const PROBLEM_MESSAGES = {
  ACCOUNT_RESTRICTED: 'profile.restricted',
  VALIDATION_FAILED: 'profile.validationFailed',
} as const

function useOwnProfile(userId: string): UseQueryResult<OwnProfile> {
  return useQuery({ queryKey: profileQueryKey(userId), queryFn: ({ signal }) => fetchOwnProfile(signal) })
}

/** Edição do próprio perfil (API-P01/P02). Carregando, erro e formulário são estados distintos (spec 13.2). */
export function ProfilePage(): React.JSX.Element {
  const account = useAccount()
  const profile = useOwnProfile(account.id)
  const { t } = useI18n()

  return (
    <Page title={t('profile.title')} intro={t('profile.intro')}>
      <ProfileBody
        profile={profile.data}
        failure={profile.isError ? profile.error : null}
        onRetry={() => {
          void profile.refetch()
        }}
        userId={account.id}
      />
      <p>
        <TextLink to={paths.account}>{t('profile.backToAccount')}</TextLink>
      </p>
    </Page>
  )
}

interface ProfileBodyProps {
  readonly profile: OwnProfile | undefined
  readonly failure: Error | null
  readonly onRetry: () => void
  readonly userId: string
}

function ProfileBody({ profile, failure, onRetry, userId }: ProfileBodyProps): React.JSX.Element {
  const { t } = useI18n()
  if (profile === undefined && failure !== null) {
    return (
      <div className="flex flex-col items-start gap-4">
        <p className="text-text-muted">{t('profile.loadError')}</p>
        <RequestFailureAlert failure={classifyRequestFailure(failure)} />
        <Button variant="secondary" onClick={onRetry}>
          {t('common.retry')}
        </Button>
      </div>
    )
  }
  if (profile === undefined) {
    return (
      <p role="status" className="text-text-muted">
        {t('profile.loading')}
      </p>
    )
  }
  return <ProfileForm initial={profile} userId={userId} />
}

function toFormValues(profile: OwnProfile): ProfileFormInput {
  return { displayName: profile.displayName ?? '', bio: profile.bio ?? '' }
}

/**
 * A versão-base é a do perfil sobre o qual a pessoa está editando, guardada no estado do formulário: uma releitura
 * em segundo plano não a troca, senão o próximo envio sobrescreveria em silêncio o que outra aba gravou (T-19).
 * Ela só muda ao salvar ou quando a pessoa pede a versão atual depois de um conflito.
 */
function ProfileForm({ initial, userId }: { readonly initial: OwnProfile; readonly userId: string }) {
  const queryClient = useQueryClient()
  const { t } = useI18n()
  const [base, setBase] = useState(initial)
  const [reloaded, setReloaded] = useState(false)
  const form = useForm<ProfileFormInput, unknown, ProfileFormValues>({
    resolver: zodResolver(profileFormSchema),
    defaultValues: toFormValues(initial),
  })
  const save = useMutation({
    mutationFn: ({ values, edited }: { readonly values: ProfileFormValues; readonly edited: EditedFields }) =>
      updateOwnProfile(base.version, toProfileChanges(values, edited)),
    onSuccess: (saved) => {
      queryClient.setQueryData(profileQueryKey(userId), saved)
      setBase(saved)
      setReloaded(false)
      form.reset(toFormValues(saved))
    },
    onError: (error) => {
      if (isUnauthorized(error)) {
        void queryClient.invalidateQueries({ queryKey: meQueryKey })
      }
    },
  })
  const reload = useMutation({
    mutationFn: () =>
      queryClient.query({ queryKey: profileQueryKey(userId), queryFn: () => fetchOwnProfile(), staleTime: 0 }),
    onSuccess: (latest) => {
      setBase(latest)
      setReloaded(true)
      save.reset()
    },
  })
  // Lido no render: o formState do react-hook-form só calcula o que é lido durante a renderização.
  const { dirtyFields } = form.formState
  const submit = form.handleSubmit((values) => {
    if (!save.isPending) {
      save.mutate({ values, edited: { ...dirtyFields } })
    }
  })

  const failure = save.isError ? classifyRequestFailure(save.error) : null
  const errorText = (message: string | undefined) => translateProfileError(t, message)
  return (
    <>
      {save.isSuccess ? (
        <Alert variant="success" focusOnMount>
          {t('profile.saved')}
        </Alert>
      ) : null}
      {failure === null ? null : (
        <SaveFailure
          failure={failure}
          reloading={reload.isPending}
          onReload={() => {
            if (!reload.isPending) {
              reload.mutate()
            }
          }}
        />
      )}
      {reload.isError ? <RequestFailureAlert failure={classifyRequestFailure(reload.error)} /> : null}
      {reloaded ? (
        <SavedVersion
          profile={base}
          onDiscard={() => {
            form.reset(toFormValues(base))
            setReloaded(false)
          }}
        />
      ) : null}
      <form
        noValidate
        method="post"
        aria-busy={save.isPending}
        className="flex flex-col gap-6"
        onSubmit={(event) => {
          void submit(event)
        }}
      >
        <TextField
          label={t('profile.displayName')}
          hint={t('profile.displayNameHint', { max: DISPLAY_NAME_MAX_LENGTH })}
          autoComplete="nickname"
          error={errorText(form.formState.errors.displayName?.message)}
          {...form.register('displayName')}
        />
        <TextAreaField
          label={t('profile.bio')}
          hint={t('profile.bioHint', { max: BIO_MAX_LENGTH })}
          rows={5}
          error={errorText(form.formState.errors.bio?.message)}
          {...form.register('bio')}
        />
        <SubmitButton pending={save.isPending} label={t('profile.save')} pendingLabel={t('profile.saving')} />
      </form>
    </>
  )
}

interface SaveFailureProps {
  readonly failure: RequestFailure
  readonly reloading: boolean
  readonly onReload: () => void
}

/** 412 é conflito de edição, não erro genérico: explica e oferece a versão atual (spec 13.2, "Conflito de perfil"). */
function SaveFailure({ failure, reloading, onReload }: SaveFailureProps): React.JSX.Element {
  const { t } = useI18n()
  if (failure.kind !== 'problem' || failure.status !== PRECONDITION_FAILED) {
    return <RequestFailureAlert failure={failure} problemMessages={PROBLEM_MESSAGES} />
  }
  return (
    <div className="flex flex-col items-start gap-4">
      <Alert variant="error">{t('profile.conflict')}</Alert>
      <Button variant="secondary" aria-disabled={reloading} onClick={onReload}>
        {reloading ? t('profile.reloading') : t('profile.reload')}
      </Button>
    </div>
  )
}

interface SavedVersionProps {
  readonly profile: OwnProfile
  readonly onDiscard: () => void
}

/** A divergência à vista: o que está salvo agora, lado a lado com o rascunho que continua no formulário. */
function SavedVersion({ profile, onDiscard }: SavedVersionProps): React.JSX.Element {
  const { t } = useI18n()
  return (
    <div className="flex flex-col items-start gap-4">
      <Alert variant="info">
        <p>{t('profile.reloaded')}</p>
        <dl className="mt-3 grid grid-cols-1 gap-x-6 gap-y-1 sm:grid-cols-[max-content_1fr]">
          <dt className="font-semibold">{t('profile.savedDisplayName')}</dt>
          <dd className="break-words">{profile.displayName ?? t('profile.emptyValue')}</dd>
          <dt className="font-semibold">{t('profile.savedBio')}</dt>
          <dd className="break-words whitespace-pre-line">{profile.bio ?? t('profile.emptyValue')}</dd>
        </dl>
      </Alert>
      <Button variant="secondary" onClick={onDiscard}>
        {t('profile.discardChanges')}
      </Button>
    </div>
  )
}

const PROFILE_ERROR_KEYS: ReadonlySet<string> = new Set<ProfileErrorKey>([
  'profile.error.displayNameRequired',
  'profile.error.displayNameTooLong',
  'profile.error.bioTooLong',
  'profile.error.invalidCharacters',
])

function isProfileErrorKey(message: string): message is ProfileErrorKey {
  return PROFILE_ERROR_KEYS.has(message)
}

/** O schema guarda a chave; aqui ela vira texto no idioma ativo. Mensagem fora do catálogo vira aviso genérico. */
function translateProfileError(t: Translate, message: string | undefined): string | undefined {
  if (message === undefined) {
    return undefined
  }
  if (!isProfileErrorKey(message)) {
    return t('validation.invalidValue')
  }
  switch (message) {
    case 'profile.error.displayNameTooLong':
      return t(message, { max: DISPLAY_NAME_MAX_LENGTH })
    case 'profile.error.bioTooLong':
      return t(message, { max: BIO_MAX_LENGTH })
    case 'profile.error.displayNameRequired':
    case 'profile.error.invalidCharacters':
      return t(message)
  }
}
