// @vitest-environment node
import { describe, expectTypeOf, it } from 'vitest'
import type { IcuArguments } from './icu-arguments'

// Testes de tipo: o `tsc` do typecheck é quem os verifica; o Vitest só os executa vazios.
describe('IcuArguments', () => {
  it('mensagem sem argumento não pede valores', () => {
    expectTypeOf<IcuArguments<'Entrar'>>().toBeNever()
  })

  it('argumento simples aceita texto ou número', () => {
    expectTypeOf<IcuArguments<'Página carregada: {title}'>>().toEqualTypeOf<{ readonly title: string | number }>()
  })

  it('vários argumentos simples, com espaços dentro das chaves', () => {
    expectTypeOf<IcuArguments<'Entre { min } e {max} caracteres.'>>().toEqualTypeOf<{
      readonly min: string | number
      readonly max: string | number
    }>()
  })

  it('number, plural e selectordinal pedem número; date e time pedem data', () => {
    expectTypeOf<IcuArguments<'{n, number} {d, date, short} {h, time}'>>().toEqualTypeOf<{
      readonly n: number
      readonly d: Date | number
      readonly h: Date | number
    }>()
  })

  it('plural pede número e não confunde o corpo das opções com argumento', () => {
    expectTypeOf<IcuArguments<'{count, plural, one {# segundo} other {segundos}}'>>().toEqualTypeOf<{
      readonly count: number
    }>()
  })

  it('select pede texto e acha argumentos aninhados nas opções', () => {
    expectTypeOf<
      IcuArguments<'espere {unit, select, seconds {{count, plural, one {# s} other {# s}}} other {um pouco}} agora, {name}'>
    >().toEqualTypeOf<{
      readonly unit: string
      readonly count: number
      readonly name: string | number
    }>()
  })

  it('string larga (não literal) não tem argumentos conhecidos', () => {
    expectTypeOf<IcuArguments<string>>().toBeNever()
  })
})
