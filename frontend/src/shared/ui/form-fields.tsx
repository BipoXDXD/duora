import { useId, useState, type ComponentProps } from 'react'
import { useI18n } from '@/shared/i18n/i18n-context'
import { Input } from './input'
import { Label } from './label'

interface FieldTextProps {
  /** Texto do label, sempre visível (A11Y-01). */
  readonly label: string
  /** Ajuda permanente, lida junto com o campo. */
  readonly hint?: string | undefined
  /** Mensagem de erro em texto; quando presente, o campo fica `aria-invalid`. */
  readonly error?: string | undefined
}

type TextFieldProps = FieldTextProps & Omit<ComponentProps<'input'>, 'id' | 'aria-invalid' | 'aria-describedby'>

/** Ids da ajuda e do erro e o `aria-describedby` que os liga ao campo, na ordem de leitura. */
function useFieldIds(hint: string | undefined, error: string | undefined) {
  const id = useId()
  const hintId = `${id}-ajuda`
  const errorId = `${id}-erro`
  const describedBy = [hint === undefined ? null : hintId, error === undefined ? null : errorId]
    .filter((value) => value !== null)
    .join(' ')
  return {
    inputId: id,
    hintId,
    errorId,
    inputAria: {
      'aria-describedby': describedBy === '' ? undefined : describedBy,
      'aria-invalid': error === undefined ? undefined : true,
    },
  }
}

function FieldHint({ id, text }: { readonly id: string; readonly text: string | undefined }): React.JSX.Element | null {
  return text === undefined ? null : (
    <p id={id} className="text-sm text-text-muted">
      {text}
    </p>
  )
}

function FieldError({
  id,
  text,
}: {
  readonly id: string
  readonly text: string | undefined
}): React.JSX.Element | null {
  return text === undefined ? null : (
    <p id={id} className="text-sm font-semibold text-danger">
      {text}
    </p>
  )
}

/** Campo de texto com label persistente, ajuda e erro associados (A11Y-01). */
export function TextField({ label, hint, error, ...inputProps }: TextFieldProps): React.JSX.Element {
  const { inputId, hintId, errorId, inputAria } = useFieldIds(hint, error)
  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor={inputId}>{label}</Label>
      <FieldHint id={hintId} text={hint} />
      <Input id={inputId} {...inputAria} {...inputProps} />
      <FieldError id={errorId} text={error} />
    </div>
  )
}

type PasswordFieldProps = FieldTextProps &
  Omit<ComponentProps<'input'>, 'id' | 'type' | 'aria-invalid' | 'aria-describedby'>

/**
 * Senha com botão "mostrar" no padrão toggle (aria-pressed, nome fixo). Colar e gerenciadores de
 * senha continuam funcionando: nada de bloquear paste nem de trocar o autocomplete.
 */
export function PasswordField({ label, hint, error, ...inputProps }: PasswordFieldProps): React.JSX.Element {
  const { inputId, hintId, errorId, inputAria } = useFieldIds(hint, error)
  const [visible, setVisible] = useState(false)
  const { t } = useI18n()
  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor={inputId}>{label}</Label>
      <FieldHint id={hintId} text={hint} />
      <div className="flex items-stretch gap-2">
        <Input
          id={inputId}
          type={visible ? 'text' : 'password'}
          autoCapitalize="off"
          autoCorrect="off"
          spellCheck={false}
          {...inputAria}
          {...inputProps}
        />
        <button
          type="button"
          aria-label={t('field.showPasswordName')}
          aria-pressed={visible}
          aria-controls={inputId}
          onClick={() => {
            setVisible((current) => !current)
          }}
          className="inline-flex min-h-11 min-w-11 shrink-0 items-center justify-center rounded-md border border-border bg-surface px-3 font-semibold text-action hover:bg-surface-muted focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
        >
          {t('field.showPassword')}
        </button>
      </div>
      <FieldError id={errorId} text={error} />
    </div>
  )
}

type CheckboxFieldProps = Omit<FieldTextProps, 'hint'> &
  Omit<ComponentProps<'input'>, 'id' | 'type' | 'aria-invalid' | 'aria-describedby'>

/** Caixa de seleção nativa na cor da marca, com a linha inteira clicável (alvo de 44 px). */
export function CheckboxField({ label, error, ...inputProps }: CheckboxFieldProps): React.JSX.Element {
  const { inputId, errorId, inputAria } = useFieldIds(undefined, error)
  return (
    <div className="flex flex-col gap-1">
      <div className="flex min-h-11 items-start gap-3 py-2">
        <input
          id={inputId}
          type="checkbox"
          className="mt-0.5 size-5 shrink-0 accent-action focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
          {...inputAria}
          {...inputProps}
        />
        <label htmlFor={inputId} className="text-text">
          {label}
        </label>
      </div>
      <FieldError id={errorId} text={error} />
    </div>
  )
}
