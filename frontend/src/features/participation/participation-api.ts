import { z } from 'zod'
import { apiClient, sendMutation } from '@/shared/api'
import type { components } from '@/shared/api/schema'

const eventSchema = z.object({
  eventId: z.string(),
  title: z.string(),
  startsAt: z.string(),
  checkInOpensAt: z.string(),
  state: z.string(),
}) satisfies z.ZodType<components['schemas']['EventResponse']>
const participationSchema = z.object({ state: z.string(), roomId: z.string().nullable() })
const cardSchema = z.object({
  cardId: z.string(),
  position: z.number(),
  prompt: z.string(),
  options: z.array(z.object({ optionId: z.string(), label: z.string() })),
})
const gameSchema = z.object({
  title: z.string(),
  objective: z.string(),
  currentCard: cardSchema.nullable(),
  awaitingPartner: z.boolean(),
  revealed: z.array(
    z.object({
      cardId: z.string(),
      prompt: z.string(),
      myOptionLabel: z.string(),
      partnerOptionLabel: z.string(),
      choseTheSame: z.boolean(),
    }),
  ),
  passed: z.array(z.object({ cardId: z.string(), prompt: z.string() })),
  availableActions: z.array(z.string()),
})
const roomSchema = z.object({
  roomId: z.string(),
  state: z.string(),
  version: z.string(),
  communicationLanguage: z.enum(['en', 'pt']),
  communicationPolicyVersion: z.number(),
  displayLocale: z.enum(['en-US', 'pt-BR']),
  invitationExpiresAt: z.string(),
  game: gameSchema.nullable(),
  audio: z
    .array(
      z.object({
        cueId: z.string(),
        assetId: z.string(),
        transcript: z.string(),
        accessibleDescription: z.string(),
        sha256: z.string(),
        url: z.string().regex(/^\/api\/v1\/rooms\/[^/]+\/audio\/[^/]+$/),
      }),
    )
    .default([]),
}) satisfies z.ZodType<
  Omit<components['schemas']['RoomResponse'], 'game'> & {
    game: Pick<
      components['schemas']['GameResponse'],
      'title' | 'objective' | 'currentCard' | 'awaitingPartner' | 'revealed' | 'passed' | 'availableActions'
    > | null
  }
>
export type Room = z.output<typeof roomSchema>
export type Event = z.output<typeof eventSchema>
export async function fetchEvents(signal?: AbortSignal): Promise<Event[]> {
  return z.array(eventSchema).parse(
    await apiClient.requestJson({
      method: 'GET',
      path: '/api/v1/events',
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}
export async function fetchParticipation(
  id: string,
  signal?: AbortSignal,
): Promise<z.output<typeof participationSchema>> {
  return participationSchema.parse(
    await apiClient.requestJson({
      method: 'GET',
      path: `/api/v1/events/${encodeURIComponent(id)}/participation`,
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}
export async function fetchRoom(id: string, signal?: AbortSignal): Promise<Room> {
  return roomSchema.parse(
    await apiClient.requestJson({
      method: 'GET',
      path: `/api/v1/rooms/${encodeURIComponent(id)}`,
      ...(signal === undefined ? {} : { signal }),
    }),
  )
}
export async function register(id: string): Promise<void> {
  return sendMutation({
    method: 'PUT',
    path: `/api/v1/events/${encodeURIComponent(id)}/registration`,
    headers: { 'Idempotency-Key': crypto.randomUUID() },
  })
}
export async function checkIn(id: string): Promise<void> {
  return sendMutation({
    method: 'POST',
    path: `/api/v1/events/${encodeURIComponent(id)}/check-in`,
    headers: { 'Idempotency-Key': crypto.randomUUID() },
  })
}
export async function roomCommand(id: string, command: 'accept' | 'reject' | 'exit'): Promise<void> {
  return sendMutation({
    method: 'POST',
    path: `/api/v1/rooms/${encodeURIComponent(id)}/${command}`,
    headers: { 'Idempotency-Key': crypto.randomUUID() },
  })
}
export async function act(
  room: Room,
  choice: { type: 'choose-option' | 'skip-card'; cardId: string; optionId?: string },
): Promise<void> {
  const { type, cardId, optionId } = choice
  const actionId = crypto.randomUUID()
  return sendMutation({
    method: 'POST',
    path: `/api/v1/rooms/${encodeURIComponent(room.roomId)}/actions`,
    headers: { 'Idempotency-Key': actionId },
    body: { actionId, expectedVersion: room.version, type, cardId, ...(optionId === undefined ? {} : { optionId }) },
  })
}
