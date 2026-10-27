import type { EngineInterface, Register, SessionRateLimit } from 'claude-code'

// Saves the plan usage Claude Code already measures (the figures its status line shows) under `latest` in this
// plugin's store, ~\.claude\plugins\store\roster-usage_<marketplace>-<hash>.json, where Claude Roster reads it.
// It makes no request of its own and saves nothing about the conversation: the windows, their percentages and resets.

/** What Claude Roster reads: when the figures were last confirmed, and each window as Claude Code reported it. */
export type Latest = {
  at: number
  limits: Array<{ kind: string; percentUsed: number; resetsAt?: string }>
}

const KEY_LATEST = 'latest'

/**
 * Only the desktop app's own Code sessions are signed in to the account Claude Roster switches. The terminal `claude`
 * keeps a sign-in of its own, maybe another account's, so its figures are never saved.
 */
async function isDesktopApp($: EngineInterface): Promise<boolean> {
  return (await $.env.get('CLAUDE_CODE_ENTRYPOINT')) === 'claude-desktop'
}

async function save($: EngineInterface, limits: readonly SessionRateLimit[]): Promise<void> {
  // Empty before the first response of a session, and off a subscription: nothing to say then.
  if (!limits.length || !(await isDesktopApp($))) return
  const latest: Latest = {
    at: await $.clock.now(),
    limits: limits.map(({ kind, percentUsed, resetsAt }) => (resetsAt ? { kind, percentUsed, resetsAt } : { kind, percentUsed })),
  }
  await $.store.set(KEY_LATEST, latest)
}

export const register: Register = on => {
  // After each turn, and whenever a window moves a whole point: each measurement re-confirms the figures, so `at`
  // stays fresh even while the percentages hold still.
  on('session.measure', async ($, e, next) => {
    const result = await next(e)
    await save($, e.rateLimits)
    return result
  })

  // A session that opens on figures already known (a resumed one) saves them straight away.
  on('session.start', async ($, e, next) => {
    const result = await next(e)
    await save($, (await $.session.usage()).rateLimits)
    return result
  })
}
