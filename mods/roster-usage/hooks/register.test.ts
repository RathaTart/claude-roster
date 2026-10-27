import { expect, test } from 'claude-code/testing'

const LIMITS = [
  { kind: 'five_hour', percentUsed: 21, resetsAt: '2026-10-27T12:40:00Z' },
  { kind: 'seven_day', percentUsed: 55 },
]

const MEASURE = {
  context: { window: 200000 },
  rateLimits: LIMITS,
  changed: ['rateLimits'],
} as const

test('a measurement in the desktop app saves the limits and when', async ($, on) => {
  // Nothing sits beneath the plugin: each hook here stands for the engine.
  const saved: Array<{ key: string; value: unknown }> = []
  on('env.get', () => ({ value: 'claude-desktop' }))
  on('clock.now', () => ({ value: 1793091282316 }))
  on('store.set', (_, e) => (saved.push({ key: e.key, value: e.value }), { value: undefined }))
  on('session.measure', (_, e) => ({ changed: e.changed }))

  await $.session.measure(MEASURE)

  expect(saved).toEqual([{ key: 'latest', value: { at: 1793091282316, limits: LIMITS } }])
})

test('the terminal claude, maybe another account, saves nothing', async ($, on) => {
  const saved: unknown[] = []
  on('env.get', () => ({ value: 'cli' }))
  on('clock.now', () => ({ value: 1 }))
  on('store.set', (_, e) => (saved.push(e), { value: undefined }))
  on('session.measure', (_, e) => ({ changed: e.changed }))

  await $.session.measure(MEASURE)

  expect(saved).toEqual([])
})

test('no limits reported (before the first reply, or off a subscription) saves nothing', async ($, on) => {
  const saved: unknown[] = []
  on('env.get', () => ({ value: 'claude-desktop' }))
  on('store.set', (_, e) => (saved.push(e), { value: undefined }))
  on('session.measure', (_, e) => ({ changed: e.changed }))

  await $.session.measure({ ...MEASURE, rateLimits: [], changed: ['context'] })

  expect(saved).toEqual([])
})
