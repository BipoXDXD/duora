// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { createTranslator } from '@/shared/i18n/translator'
import { translateValidationMessage } from './validation-message'

describe('translateValidationMessage', () => {
  const pt = createTranslator('pt-BR')
  const en = createTranslator('en-US')

  it('sem erro, sem mensagem', () => {
    expect(translateValidationMessage(pt, undefined)).toBeUndefined()
  })

  it('traduz a chave guardada pelo schema no idioma pedido', () => {
    expect(translateValidationMessage(pt, 'validation.emailRequired')).toBe('Informe seu e-mail.')
    expect(translateValidationMessage(en, 'validation.emailRequired')).toBe('Enter your email.')
  })

  it('preenche os limites do contrato nas mensagens que os citam', () => {
    expect(translateValidationMessage(pt, 'validation.passwordTooShort')).toBe(
      'A senha precisa ter pelo menos 12 caracteres.',
    )
    expect(translateValidationMessage(en, 'validation.passwordTooLong')).toBe(
      'Your password can be at most 128 characters long.',
    )
    expect(translateValidationMessage(en, 'validation.emailTooLong')).toBe(
      'Your email can be at most 254 characters long.',
    )
  })

  it.each(['Invalid input: expected string, received undefined', 'login.title', ''])(
    'mensagem fora das chaves de validação ("%s") vira o aviso genérico traduzido',
    (message) => {
      expect(translateValidationMessage(en, message)).toBe('Check the value in this field.')
    },
  )
})
