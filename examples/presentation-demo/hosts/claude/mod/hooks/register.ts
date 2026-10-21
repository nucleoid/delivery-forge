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
    await $.command.register({ name: 'forge-cockpit-demo', description: 'Open the synthetic read-only Forge cockpit', argumentHint: '[open|view <name>|scenario <fixture>|reset|close|help]', immediate: true })
    await loadPreference($)
    return next(e)
  })

  on('classic.SessionStart', { source: ['clear', 'resume', 'fork'] }, async ($: any, e: any, next: any) => { await loadPreference($); return next(e) })

  on('command.run', { command: 'forge-cockpit-demo' }, async ($: any, e: any) => {
    const parts = commandText(e).split(/\s+/).filter(Boolean), command = parts[0] || 'open'
    if (command === 'close') { await $.ui.close({ id: PANE }); return {} }
    if (command === 'help') { await update($, notice, () => 'Views: overview, evidence, usage, activity. Fixtures are bundled and synthetic.'); await $.ui.open({ id: PANE, title: 'Forge · synthetic', focus: true, closeOnEscape: true }); return {} }
    if (command === 'reset') { await update($, scenario, () => 'failed-gate'); await update($, selected, () => 0); await setView($, 'overview') }
    else if (command === 'view') { if (!COCKPIT_VIEWS.includes(parts[1])) { await update($, notice, () => 'Unknown view. Use overview, evidence, usage, or activity.'); return {} } await setView($, parts[1]) }
    else if (command === 'scenario') { const found = states.find((x) => x.name === parts[1]); if (!found) { await update($, notice, () => 'Unknown bundled fixture.'); return {} } await update($, scenario, () => found.name); await update($, selected, () => 0); await update($, detail, () => false) }
    else if (command !== 'open') { await update($, notice, () => 'Unknown command. No action taken.'); return {} }
    await $.ui.open({ id: PANE, title: 'Forge · synthetic', focus: true, closeOnEscape: true })
    return {}
  })

  on('ui.render', { component: 'Pane' }, async ($: any, e: any, next: any) => {
    if (e.requestId !== PANE) return next(e)
    const { Box, Text, Button, Select } = $.ui.resolve(e)
    const currentView = await read($, view), currentScenario = await read($, scenario), currentSelected = await read($, selected), showDetail = await read($, detail), message = await read($, notice)
    const state = findState(currentScenario), safeSelected = Math.max(0, Math.min(state.evidence.length - 1, currentSelected)), vm = { ...state, selected: safeSelected, detail: state.details[safeSelected] || ['No evidence selected.'] }
    const width = Number(e?.props?.bodyColumns) || 72
    const bodyRows = Number(e?.props?.scroll?.bodyRows) || 12
    const redraw = () => $.ui.invalidate('ui.render')
    const chooseView = (name: string, label: string, hotkey: string) => Button({ key: `view-${name}`, label, hotkey, plain: true, dimColor: currentView !== name, onPress: async () => { await setView($, name); redraw() } })
    const header = vm.header.slice(0, width < 32 || bodyRows < 12 ? 4 : 5).map((value: string, i: number) => Text({ key: `header-${i}`, bold: i === 0 || i === 2, dimColor: i === 4, children: [value] }))
    let body: any[] = []
    if (currentView === 'evidence' && showDetail) {
      body = [Button({ key: 'evidence-back', label: '< Back to Evidence', onPress: async () => { await update($, detail, () => false); redraw() } }), ...vm.detail.map((value: string, i: number) => Text({ key: `detail-${i}`, bold: i === 0, children: [value] }))]
    } else if (currentView === 'evidence') {
      const options = vm.evidence.map((row: any) => ({ value: row.id, label: row.text }))
      const openEvidence = async (value: string) => { const index = vm.evidence.findIndex((x: any) => x.id === value); await update($, selected, () => Math.max(0, index)); await update($, detail, () => true); redraw() }
      body = !options.length ? [Text({ children: ['No evidence receipts in this fixture.'] })]
        : width < 32 ? [Select({ key: 'evidence-select', value: options[safeSelected]?.value, options, onSelect: openEvidence })]
        : options.map((option: any) => Button({ key: `evidence-${option.value}`, label: option.label, plain: true, onPress: () => openEvidence(option.value) }))
    } else body = (state[currentView] || state.overview).map((value: string, i: number) => Text({ key: `${currentView}-${i}`, children: [value] }))
    const tabs = width < 32
      ? Select({ key: 'view-select', options: COCKPIT_VIEWS.map((name) => ({ value: name, label: name[0].toUpperCase() + name.slice(1) })), onSelect: async (name: string) => { await setView($, name); redraw() } })
      : Box({ flexDirection: 'row', columnGap: width >= 72 ? 3 : 1, children: [chooseView('overview', 'Overview', '1'), chooseView('evidence', 'Evidence', '2'), chooseView('usage', 'Usage', '3'), chooseView('activity', 'Activity', '4')] })
    return Box({ flexDirection: 'column', children: [...header, tabs, ...(message ? [Text({ dimColor: true, children: [message] })] : []), ...body, Text({ dimColor: true, children: ['Read-only synthetic fixture · no execution or publication controls'] })] })
  })
}
