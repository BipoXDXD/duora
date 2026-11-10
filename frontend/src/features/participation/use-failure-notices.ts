import { useState } from 'react'

/** `awaiting`: o render ainda não mostrou o estado relido; `current`: mostra; `dropped`: o estado já mudou depois. */
type Phase = 'awaiting' | 'current' | 'dropped'

interface FailureNotice {
  readonly state: string
  readonly phase: Phase
}

export interface FailureNotices<TSource extends string> {
  /** Grava o estado relido do cache logo depois da falha, antes de a mutação expor `isError`. */
  readonly record: (source: TSource, stateAtFailure: string) => void
  /** A última falha da fonte ainda descreve a sala; sem estado gravado (releitura indisponível), vale. */
  readonly isCurrent: (source: TSource) => boolean
}

/**
 * A falha de uma mutação vale para o estado da sala relido logo depois dela, até a primeira mudança de estado depois de
 * aparecer. O estado vem do cache, não do render: o filho pode renderizar a falha antes de o pai receber a releitura.
 */
export function useFailureNotices<TSource extends string>(roomState: string): FailureNotices<TSource> {
  const [notices, setNotices] = useState<ReadonlyMap<TSource, FailureNotice>>(new Map())
  const advanced = advanceAll(notices, roomState)
  if (advanced !== notices) {
    setNotices(advanced)
  }
  return {
    record: (source, stateAtFailure) => {
      setNotices((previous) => new Map(previous).set(source, { state: stateAtFailure, phase: 'awaiting' }))
    },
    isCurrent: (source) => {
      const notice = advanced.get(source)
      return notice === undefined || notice.phase === 'current'
    },
  }
}

function advanceAll<TSource extends string>(
  notices: ReadonlyMap<TSource, FailureNotice>,
  roomState: string,
): ReadonlyMap<TSource, FailureNotice> {
  const entries = [...notices].map(([source, notice]) => [source, advance(notice, roomState)] as const)
  const changed = entries.some(([source, notice]) => notice !== notices.get(source))
  return changed ? new Map(entries) : notices
}

function advance(notice: FailureNotice, roomState: string): FailureNotice {
  if (notice.phase === 'awaiting' && roomState === notice.state) return { ...notice, phase: 'current' }
  if (notice.phase === 'current' && roomState !== notice.state) return { ...notice, phase: 'dropped' }
  return notice
}
