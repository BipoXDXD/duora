import type { MessageKey } from '@/shared/i18n/catalog'
import { ptBR } from '@/shared/i18n/catalog.pt-BR'
import { useI18n } from '@/shared/i18n/i18n-context'
import type { Translate } from '@/shared/i18n/translator'
import { EMAIL_MAX_LENGTH, PASSWORD_MAX_LENGTH, PASSWORD_MIN_LENGTH } from './auth-schemas'

/** Chaves `validation.*`: os schemas guardam a chave como mensagem e a tela traduz no idioma ativo. */
export type ValidationKey = Extract<MessageKey, `validation.${string}`>

const VALIDATION_KEYS: ReadonlySet<string> = new Set(Object.keys(ptBR).filter((key) => key.startsWith('validation.')))

function isValidationKey(message: string): message is ValidationKey {
  return VALIDATION_KEYS.has(message)
}

/**
 * Traduz a mensagem de erro de um campo. Guardar a chave, e não o texto, faz o erro já exibido
 * mudar de idioma junto com a interface. Uma mensagem fora do catálogo (texto padrão do Zod, em
 * inglês) vira um aviso genérico traduzido em vez de aparecer crua.
 */
export function translateValidationMessage(t: Translate, message: string | undefined): string | undefined {
  if (message === undefined) {
    return undefined
  }
  if (!isValidationKey(message)) {
    return t('validation.invalidValue')
  }
  switch (message) {
    case 'validation.emailTooLong':
      return t(message, { max: EMAIL_MAX_LENGTH })
    case 'validation.passwordTooShort':
      return t(message, { min: PASSWORD_MIN_LENGTH })
    case 'validation.passwordTooLong':
      return t(message, { max: PASSWORD_MAX_LENGTH })
    case 'validation.emailRequired':
    case 'validation.emailInvalid':
    case 'validation.passwordRequired':
    case 'validation.termsRequired':
    case 'validation.privacyRequired':
    case 'validation.passwordMismatch':
    case 'validation.totpCodeInvalid':
    case 'validation.recoveryCodeInvalid':
    case 'validation.invalidValue':
      return t(message)
  }
}

/** `fieldError(errors.email?.message)` no idioma ativo. */
export function useValidationMessage(): (message: string | undefined) => string | undefined {
  const { t } = useI18n()
  return (message) => translateValidationMessage(t, message)
}
