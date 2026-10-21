// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { createEventDateTimeFormatter } from './translator'

const instants = ['2026-03-08T06:59:00Z', '2026-03-08T07:00:00Z', '2026-11-01T05:30:00Z', '2026-11-01T06:30:00Z']

describe('approved generic event zones with instant-specific UTC offsets', () => {
  it.each([
    [
      'pt-BR',
      'America/New_York',
      [
        'Horário do evento: 08/03/2026, 01:59 — Horário Nova York · UTC−05:00',
        'Horário do evento: 08/03/2026, 03:00 — Horário Nova York · UTC−04:00',
        'Horário do evento: 01/11/2026, 01:30 — Horário Nova York · UTC−04:00',
        'Horário do evento: 01/11/2026, 01:30 — Horário Nova York · UTC−05:00',
      ],
    ],
    [
      'en-US',
      'America/New_York',
      [
        'Event time: 3/8/26, 1:59 AM — ET · UTC−05:00',
        'Event time: 3/8/26, 3:00 AM — ET · UTC−04:00',
        'Event time: 11/1/26, 1:30 AM — ET · UTC−04:00',
        'Event time: 11/1/26, 1:30 AM — ET · UTC−05:00',
      ],
    ],
    [
      'pt-BR',
      'America/Sao_Paulo',
      [
        'Horário do evento: 08/03/2026, 03:59 — BRT · UTC−03:00',
        'Horário do evento: 08/03/2026, 04:00 — BRT · UTC−03:00',
        'Horário do evento: 01/11/2026, 02:30 — BRT · UTC−03:00',
        'Horário do evento: 01/11/2026, 03:30 — BRT · UTC−03:00',
      ],
    ],
    [
      'en-US',
      'America/Sao_Paulo',
      [
        'Event time: 3/8/26, 3:59 AM — São Paulo Time · UTC−03:00',
        'Event time: 3/8/26, 4:00 AM — São Paulo Time · UTC−03:00',
        'Event time: 11/1/26, 2:30 AM — São Paulo Time · UTC−03:00',
        'Event time: 11/1/26, 3:30 AM — São Paulo Time · UTC−03:00',
      ],
    ],
    [
      'pt-BR',
      'UTC',
      [
        'Horário do evento: 08/03/2026, 06:59 UTC',
        'Horário do evento: 08/03/2026, 07:00 UTC',
        'Horário do evento: 01/11/2026, 05:30 UTC',
        'Horário do evento: 01/11/2026, 06:30 UTC',
      ],
    ],
    [
      'en-US',
      'UTC',
      [
        'Event time: 3/8/26, 6:59 AM UTC',
        'Event time: 3/8/26, 7:00 AM UTC',
        'Event time: 11/1/26, 5:30 AM UTC',
        'Event time: 11/1/26, 6:30 AM UTC',
      ],
    ],
  ] as const)('%s / %s', (locale, zone, expected) => {
    const format = createEventDateTimeFormatter(locale, zone)
    instants.forEach((value, index) => {
      const instant = new Date(value)
      expect(format(instant, zone)).toBe(expected[index])
      expect(instant.toISOString()).toBe(value.replace('Z', '.000Z'))
    })
  })

  it('labels a saved choice explicitly and keeps both dates across midnight', () => {
    expect(
      createEventDateTimeFormatter('pt-BR', 'America/New_York', 'preference')(new Date('2026-10-17T02:30Z'), 'UTC'),
    ).toBe(
      'Horário do evento: 17/10/2026, 02:30 UTC\nNo seu fuso salvo: 16/10/2026, 22:30 — Horário Nova York · UTC−04:00',
    )
    expect(
      createEventDateTimeFormatter('en-US', 'America/New_York', 'preference')(new Date('2026-10-17T02:30Z'), 'UTC'),
    ).toBe('Event time: 10/17/26, 2:30 AM UTC\nIn your saved time zone: 10/16/26, 10:30 PM — ET · UTC−04:00')
  })

  it('does not duplicate an offset-only fallback and keeps fractional-hour offsets', () => {
    const instant = new Date('2026-10-16T23:00Z')
    expect(createEventDateTimeFormatter('pt-BR', 'Etc/GMT+3')(instant, 'Etc/GMT+3')).toBe(
      'Horário do evento: 16/10/2026, 20:00 UTC−03:00',
    )
    expect(createEventDateTimeFormatter('en-US', 'Asia/Kathmandu')(instant, 'Asia/Kathmandu')).toContain('UTC+05:45')
  })

  it('shows an explicit UTC reference instead of crashing for an unsupported event zone', () => {
    expect(createEventDateTimeFormatter('pt-BR', 'UTC')(new Date('2026-10-16T23:00Z'), 'Mars/Olympus')).toBe(
      'Referência: 16/10/2026, 23:00 UTC; este dispositivo não reconhece o fuso Mars/Olympus',
    )
  })
})
