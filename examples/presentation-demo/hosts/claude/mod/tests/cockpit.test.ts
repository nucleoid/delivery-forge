import { expect, test } from 'claude-code/testing'

const PANE = {
  plugin: 'forge-cockpit-demo', component: 'Pane', requestId: 'forge-cockpit-demo', surface: 'terminal',
  viewport: { columns: 120, rows: 30 },
  props: { title: 'Forge · synthetic', isFocused: true, bodyColumns: 72, placement: 'dock', scroll: { offset: 0, bodyRows: 24 }, view: {} },
} as const

test('native command opens without transcript text or a model turn', async ($, on) => {
  on('session.start', () => ({ cwd: '/fixture' }))
  on('command.register', () => ({ value: undefined }))
  on('store.get', () => ({ value: { view: 'overview' } }))
  on('ui.open', () => ({ value: { isPlaced: true } }))
  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/fixture' })
  const answer = await $.command.run({ command: 'forge-cockpit-demo', args: 'open' })
  expect(answer).toEqual({})
})

test('pane switches all views and opens exact read-only evidence detail', async ($, on) => {
  on('store.set', () => ({ value: undefined }))
  const ui = await $.ui.mount(PANE)
  expect(await ui.find({ type: 'Text', text: /FORGE · demo-014 · SYNTHETIC/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /INCOMPLETE/ })).toBeDefined()
  await ui.press({ key: 'view-evidence' })
  await ui.press({ key: 'evidence-mutation' })
  expect(await ui.find({ type: 'Text', text: /receipt-mutation-attempt-1/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /does not authorize retry or publication/ })).toBeDefined()
  await ui.press({ key: 'evidence-back' })
  await ui.press({ key: 'view-usage' })
  expect(await ui.find({ type: 'Text', text: 'USD API-EQUIVALENT ESTIMATE' })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /UNPRICED/ })).toBeDefined()
  await ui.press({ key: 'view-activity' })
  expect(await ui.find({ type: 'Text', text: /fixture/ })).toBeDefined()
  await ui.unmount()
})

test('preference store denial leaves a usable honest pane', async ($, on) => {
  on('store.get', () => ({ deny: 'fixture store denied' }))
  on('session.start', () => ({ cwd: '/fixture' }))
  on('command.register', () => ({ value: undefined }))
  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/fixture' })
  const ui = await $.ui.mount(PANE)
  expect(await ui.find({ type: 'Text', text: /Preference store unavailable/ })).toBeDefined()
  expect(await ui.find({ type: 'Text', text: /INCOMPLETE/ })).toBeDefined()
  await ui.unmount()
})
