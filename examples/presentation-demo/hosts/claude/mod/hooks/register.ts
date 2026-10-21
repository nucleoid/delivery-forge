import { atom, read, update } from 'claude-code'
import { GENERATED_FIXTURES } from '../generated/fixtures.ts'

const PANE = 'forge-cockpit-demo'
const PREF = 'forge-cockpit-demo-preferences-v1'
const COCKPIT_VIEWS = ['overview', 'evidence', 'usage', 'activity']
const states = GENERATED_FIXTURES
const view = atom({ plugin: 'forge-cockpit-demo', key: 'view' }, 'overview')
const scenario = atom({ plugin: 'forge-cockpit-demo', key: 'scenario' }, 'failed-gate')
const selected = atom({ plugin: 'forge-cockpit-demo', key: 'selected' }, 0)
const detail = atom({ plugin: 'forge-cockpit-demo', key: 'detail' }, false)
const notice = atom({ plugin: 'forge-cockpit-demo', key: 'notice' }, '')

const validView = (value: unknown) => COCKPIT_VIEWS.includes(String(value)) ? String(value) : 'overview'
const findState = (name: unknown) => states.find((x) => x.name === name) || states.find((x) => x.name === 'failed-gate') || states[0]
const commandText = (e: any) => String(e?.args ?? e?.argument ?? e?.text ?? '').replace(/^\/?forge-cockpit-demo\s*/, '').trim().slice(0, 160)
const scenarioLabel = (name: string) => name.split('-').map((part) => part[0].toUpperCase() + part.slice(1)).join(' ')
const paneOptions = { id: PANE, title: 'Forge · synthetic', focus: true, closeOnEscape: true, rows: 30, columns: 88 } as const
const outcomeColor = (value: string) => /FAIL|ERROR/.test(value) ? 'red' : /INCOMPLETE|UNKNOWN|STALE|UNPRICED|PARTIAL/.test(value) ? 'yellow' : /PASS|CURRENT|COMPLETE/.test(value) ? 'green' : undefined

async function setView($: any, next: string) {
  const safe = validView(next)
  await update($, view, () => safe)
  await update($, detail, () => false)
  try { await $.store.set(PREF, { view: safe }) } catch { await update($, notice, () => 'Preference store unavailable; session state remains usable.') }
}

async function loadPreference($: any) {
  try {
    const saved = await $.store.get(PREF)
    await update($, view, () => validView(saved?.view))
    await update($, notice, () => '')
  } catch { await update($, notice, () => 'Preference store unavailable; using Overview.') }
}

export function register(on: any) {
  on('session.start', async ($: any, e: any, next: any) => {
    await $.command.register({ name: 'forge-cockpit-demo', description: 'Open the synthetic read-only Forge cockpit', immediate: true })
    await loadPreference($)
    return next(e)
  })

  on('classic.SessionStart', { source: ['clear', 'resume', 'fork'] }, async ($: any, e: any, next: any) => { await loadPreference($); return next(e) })

  on('command.run', { command: 'forge-cockpit-demo' }, async ($: any, e: any) => {
    if (commandText(e)) {
      await update($, notice, () => 'Arguments are not accepted in the terminal prompt. Use the native Fixture and view controls.')
      return {}
    }
    await update($, notice, () => '')
    await $.ui.open(paneOptions)
    return {}
  })

  on('ui.render', { component: 'Pane' }, async ($: any, e: any, next: any) => {
    if (e.requestId !== PANE) return next(e)
    const { Box, Text, Button, Select } = $.ui.resolve(e)
    const currentView = await read($, view), currentScenario = await read($, scenario), currentSelected = await read($, selected), showDetail = await read($, detail), message = await read($, notice)
    const state = findState(currentScenario), safeSelected = Math.max(0, Math.min(state.evidence.length - 1, currentSelected)), vm = { ...state, selected: safeSelected, detail: state.details[safeSelected] || ['No evidence selected.'] }
    const width = Number(e?.props?.bodyColumns) || 72
    const bodyRows = Number(e?.props?.scroll?.bodyRows) || 12
    const compact = width < 40
    const redraw = () => $.ui.invalidate('ui.render')
    const chooseView = (name: string, label: string, hotkey: string) => Button({ key: `view-${name}`, label, hotkey, plain: true, dimColor: currentView !== name, onPress: async () => { await setView($, name); redraw() } })
    const text = (key: string, value: string, props: any = {}) => Text({ key, wrap: 'wrap', ...props, children: [value] })
    const card = (key: string, title: string, children: any[]) => Box({ key, flexDirection: 'column', borderStyle: 'round', paddingX: 1, marginTop: 1, children: [text(`${key}-title`, title.toUpperCase(), { color: 'cyan', weight: 'semibold' }), ...children] })
    const header = card('summary', 'Forge cockpit', [
      Box({ key: 'identity-row', flexDirection: compact ? 'column' : 'row', columnGap: 2, children: [text('run', vm.header[0], { weight: 'bold' }), text('source-badge', 'SYNTHETIC · READ ONLY', { color: 'yellow', weight: 'semibold' })] }),
      text('phase', vm.header[1], { color: 'magenta', weight: 'semibold' }),
      text('outcome', vm.header[2], { color: outcomeColor(vm.header[2]), weight: 'bold' }),
      text('next', vm.header[3], { weight: 'semibold' }),
      ...(bodyRows >= 15 ? [text('source', vm.header[4], { dimColor: true })] : []),
    ])
    const chooseScenario = async (name: string) => {
      const found = states.find((candidate) => candidate.name === name)
      if (!found) { await update($, notice, () => 'Unknown bundled fixture. No state changed.'); redraw(); return }
      await update($, scenario, () => found.name)
      await update($, selected, () => 0)
      await update($, detail, () => false)
      await update($, notice, () => '')
      redraw()
    }
    const fixtureControl = Select({ key: 'fixture-select', label: 'Fixture', value: state.name, options: states.map((item) => ({ value: item.name, label: scenarioLabel(item.name) })), onSelect: chooseScenario })
    let body: any[] = []
    if (currentView === 'evidence' && showDetail) {
      body = [card('evidence-detail', `Evidence detail · ${safeSelected + 1} / ${vm.evidence.length}`, [
        Button({ key: 'evidence-back', label: '< Back to Evidence', onPress: async () => { await update($, detail, () => false); redraw() } }),
        ...vm.detail.map((value: string, i: number) => text(`detail-${i}`, value, { color: i === 0 ? outcomeColor(value) : undefined, weight: i === 0 ? 'bold' : 'regular', dimColor: /^artifacts:/.test(value) })),
      ])]
    } else if (currentView === 'evidence') {
      const options = vm.evidence.map((row: any) => ({ value: row.id, label: row.text }))
      const openEvidence = async (value: string) => { const index = vm.evidence.findIndex((x: any) => x.id === value); await update($, selected, () => Math.max(0, index)); await update($, detail, () => true); redraw() }
      body = [card('evidence-list', 'Evidence receipts', !options.length
        ? [text('evidence-empty', 'No evidence receipts yet · aggregate remains INCOMPLETE', { color: 'yellow', weight: 'semibold' })]
        : compact ? [Select({ key: 'evidence-select', label: 'Receipt', value: options[safeSelected]?.value, options, onSelect: openEvidence })]
        : options.map((option: any, index: number) => Button({ key: `evidence-${option.value}`, label: option.label, plain: true, dimColor: index !== safeSelected, onPress: () => openEvidence(option.value) })))]
    } else if (currentView === 'overview') {
      const identity = state.overview.slice(0, 4)
      const delivery = state.overview.slice(4, 9)
      const integrations = state.overview.slice(9, -1)
      body = [
        card('delivery', 'Decision and delivery', delivery.map((value: string, i: number) => text(`delivery-${i}`, value, { color: outcomeColor(value), weight: i < 2 ? 'semibold' : 'regular' }))),
        card('identity', 'Bound identity', identity.map((value: string, i: number) => text(`identity-${i}`, value, { dimColor: i > 0 }))),
        ...(integrations.length ? [card('integrations', 'Readiness', integrations.map((value: string, i: number) => text(`integration-${i}`, value, { color: /unavailable|not connected/i.test(value) ? 'yellow' : undefined })))] : []),
      ]
    } else if (currentView === 'usage') {
      const ledgerIndex = state.usage.findIndex((value: string) => value.startsWith('Ledger '))
      const pricingIndex = state.usage.indexOf('Pricing provenance')
      const endModels = ledgerIndex < 0 ? state.usage.length : ledgerIndex
      const endLedger = pricingIndex < 0 ? state.usage.length : pricingIndex
      body = [
        card('usage-summary', 'Usage estimate', state.usage.slice(0, 3).map((value: string, i: number) => text(`usage-summary-${i}`, value, { color: outcomeColor(value), weight: i < 2 ? 'bold' : 'regular' }))),
        card('usage-models', 'Per-model usage', state.usage.slice(3, endModels).map((value: string, i: number) => text(`usage-model-${i}`, value, { color: /UNPRICED/.test(value) ? 'yellow' : undefined }))),
        ...(ledgerIndex >= 0 ? [card('usage-ledger', 'Ledger and reconciliation', state.usage.slice(ledgerIndex, endLedger).map((value: string, i: number) => text(`usage-ledger-${i}`, value, { dimColor: i === endLedger - ledgerIndex - 1 })))] : []),
        ...(pricingIndex >= 0 ? [card('usage-provenance', 'Pricing and request provenance', state.usage.slice(pricingIndex + 1).filter((value: string) => value !== 'Request provenance').map((value: string, i: number) => text(`usage-provenance-${i}`, value, { dimColor: i > 0 })))] : []),
      ]
    } else {
      body = [card('activity-card', 'Fixture activity · newest first', state.activity.map((value: string, i: number) => text(`activity-${i}`, value, { color: outcomeColor(value), weight: i === 0 ? 'semibold' : 'regular', dimColor: i > 0 })))]
    }
    const tabs = compact
      ? Select({ key: 'view-select', label: 'View', value: currentView, options: COCKPIT_VIEWS.map((name) => ({ value: name, label: name[0].toUpperCase() + name.slice(1) })), onSelect: async (name: string) => { await setView($, name); redraw() } })
      : Box({ flexDirection: 'row', columnGap: width >= 72 ? 3 : 1, children: [chooseView('overview', 'Overview', '1'), chooseView('evidence', 'Evidence', '2'), chooseView('usage', 'Usage', '3'), chooseView('activity', 'Activity', '4')] })
    return Box({ flexDirection: 'column', rowGap: 1, children: [header, fixtureControl, tabs, ...(message ? [text('notice', message, { color: 'yellow', weight: 'semibold' })] : []), ...body, text('safety-footer', 'READ ONLY · synthetic fixture · no execution, approval, telemetry, or publication controls', { dimColor: true })] })
  })
}
