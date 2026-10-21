// @vitest-environment node
import { expect, it } from 'vitest'
import { profileFormSchema, toProfileChanges } from './profile-form'

it('sends country only without inventing a subdivision', () => {
  const values = profileFormSchema.parse({ displayName: 'Ana', bio: '', countryCode: 'BR', subdivisionCode: '' })
  expect(toProfileChanges(values, { countryCode: true })).toEqual({ countryCode: 'BR', subdivisionCode: null })
})

it('clears the whole location and preserves untouched fields', () => {
  const values = profileFormSchema.parse({ displayName: 'Ana', bio: '', countryCode: '', subdivisionCode: '' })
  expect(toProfileChanges(values, { countryCode: true })).toEqual({ countryCode: null, subdivisionCode: null })
  expect(toProfileChanges(values, { bio: true })).toEqual({ bio: null })
})

it('rejects a subdivision without a country and countries not enabled', () => {
  expect(
    profileFormSchema.safeParse({ displayName: 'Ana', bio: '', countryCode: '', subdivisionCode: 'BR-SP' }).success,
  ).toBe(false)
  expect(
    profileFormSchema.safeParse({ displayName: 'Ana', bio: '', countryCode: 'US', subdivisionCode: '' }).success,
  ).toBe(false)
})
