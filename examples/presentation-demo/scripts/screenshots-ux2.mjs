#!/usr/bin/env node
// UX-2 screenshot set: simulated-terminal frames rendered from real renderer output (ANSI converted to HTML spans),
// plus the two desktop host mockups. Every image is labelled HARNESS RENDER or DESKTOP MOCKUP; none is an installed-host capture.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { getScenario, snapshotsFromCatalog } from '../src/fixtures.mjs';
import { renderCockpit, renderEvidenceDetail, renderEvidenceList, renderStatusLine, renderUsagePanel } from '../src/render-cockpit.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const out = path.resolve(process.env.FORGE_UX2_ASSETS || path.join(root, '../../docs/ux/cockpit-ux2/assets'));
fs.mkdirSync(out, { recursive: true });
const written = [];

const esc = (t) => t.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
// Minimal SGR interpreter: 0 reset, 1 bold, 2 dim, 7 inverse, 38;2;r;g;b foreground.
function ansiToHtml(text) {
  let html = '', state = { bold: false, dim: false, inverse: false, fg: null };
  const open = () => { const styles = []; if (state.fg) styles.push(`color:${state.fg}`); if (state.bold) styles.push('font-weight:700'); if (state.dim) styles.push('opacity:.55'); if (state.inverse) styles.push(`background:${state.fg || '#E8EEF2'};color:#101820`); return styles.length ? `<span style="${styles.join(';')}">` : '<span>'; };
  const parts = text.split(/(\x1b\[[0-?]*[ -/]*[@-~])/);
  for (const part of parts) {
    if (!part) continue;
    const m = /^\x1b\[([0-9;]*)m$/.exec(part);
    if (!m) { html += `${open()}${esc(part)}</span>`; continue; }
    const codes = m[1] ? m[1].split(';').map(Number) : [0];
    for (let i = 0; i < codes.length; i++) {
      const c = codes[i];
      if (c === 0) state = { bold: false, dim: false, inverse: false, fg: null };
      else if (c === 1) state.bold = true; else if (c === 2) state.dim = true; else if (c === 7) state.inverse = true;
      else if (c === 38 && codes[i + 1] === 2) { state.fg = `rgb(${codes[i + 2]},${codes[i + 3]},${codes[i + 4]})`; i += 4; }
    }
  }
  return html;
}

const browser = await chromium.launch({ headless: true });
const TERM_CSS = `html,body{margin:0;background:#0b1218}
.wrap{display:inline-block;padding:20px}
.term{display:inline-block;background:#101820;border:1px solid #30414D;border-radius:10px;overflow:hidden;box-shadow:0 10px 30px rgba(0,0,0,.45)}
.bar{display:flex;align-items:center;gap:10px;padding:8px 14px;background:#17232D;border-bottom:1px solid #30414D;font:600 11px/1 system-ui,sans-serif;color:#AFBEC9;letter-spacing:.04em}
.bar b{color:#F2BA69;font-weight:800;letter-spacing:.08em}
.bar .dots{display:flex;gap:6px;margin-right:4px}.bar .dots i{width:10px;height:10px;border-radius:50%;background:#30414D;display:block}
pre{margin:0;padding:14px 18px;color:#E8EEF2;font:13.5px/1.42 "Cascadia Mono","JetBrains Mono",Consolas,ui-monospace,monospace;white-space:pre}
.stack{display:flex;flex-direction:column;gap:18px}
.caption{font:12px/1.4 system-ui,sans-serif;color:#AFBEC9;max-width:900px;margin-top:6px}
.caption b{color:#F2BA69}`;

async function terminal(name, blocks, { width = 1180 } = {}) {
  const page = await browser.newPage({ viewport: { width, height: 900 }, deviceScaleFactor: 2 });
  const html = blocks.map(({ title, text, caption }) => `<div><div class="term"><div class="bar"><span class="dots"><i></i><i></i><i></i></span><b>HARNESS RENDER</b><span>${esc(title)}</span><span style="margin-left:auto">simulated terminal · not an installed host</span></div><pre>${ansiToHtml(text)}</pre></div>${caption ? `<div class="caption">${caption}</div>` : ''}</div>`).join('');
  await page.setContent(`<style>${TERM_CSS}</style><div class="wrap"><div class="stack">${html}</div></div>`);
  const clip = await page.locator('.wrap').boundingBox();
  const file = path.join(out, name);
  await page.screenshot({ path: file, clip: { x: 0, y: 0, width: Math.ceil(clip.width), height: Math.ceil(clip.height) } });
  await page.close(); written.push({ file: name, kind: 'terminal-simulation', blocks: blocks.map((b) => b.title) });
}

const snap = (n) => getScenario(n).snapshot;
const C = { color: true, presentation: 'harness' };
await terminal('tui-cockpit-80-failed-gate.png', [{ title: 'node scripts/demo.mjs failed-gate 80 --color · 80 columns', text: renderCockpit(snap('failed-gate'), { ...C, width: 80 }), caption: '<b>Failed gate.</b> One frame answers the five questions: identity rail, phase trail, focus sentence, evidence rows with glyph + word, control, next action, usage with share-of-known bars, integrations, full identity footer.' }]);
await terminal('tui-cockpit-120-review.png', [{ title: 'node scripts/demo.mjs review 120 --color · 120 columns', text: renderCockpit(snap('review'), { ...C, width: 120 }), caption: '<b>Review on the exact head at 120 columns.</b> Same grammar, wider receipts; actors join the usage rows. Desired REVIEWING vs observed READY is flagged, not hidden.' }], { width: 1500 });
await terminal('tui-cockpit-80-recovery.png', [
  { title: 'cancel-unknown · stop requested, cleanup UNKNOWN', text: renderCockpit(snap('cancel-unknown'), { ...C, width: 80 }) },
  { title: 'head-drift · review belongs to an earlier head', text: renderCockpit(snap('head-drift'), { ...C, width: 80 }), caption: '<b>Recovery states.</b> Unknown cleanup turns Control rose and makes every intent UNAVAILABLE with its reason; head drift keeps the historical PASS receipts visible but marks them STALE and the aggregate INCOMPLETE.' }]);
await terminal('tui-cockpit-80-pr-receipt.png', [{ title: 'pr-receipt · PR opened, ceiling PR', text: renderCockpit(snap('pr-receipt'), { ...C, width: 80 }), caption: '<b>PR receipt.</b> Publication block states remote effect, readback, CI INCOMPLETE and merge NOT authorized. PR is not deployment.' }]);
await terminal('tui-status-lines.png', ['launch', 'execute', 'failed-gate', 'stale-reconnected', 'cancel-unknown'].map((n) => ({ title: `status line · ${n} · 80 columns (Claude Code statusLine / Pi widget)`, text: renderStatusLine(snap(n), { width: 80, color: true, presentation: 'native' }).join('\n') })).concat([{ title: '', text: '', caption: '<b>Four-line persistent status.</b> Identity, outcome glyph + word + focus sentence, synthetic API-equivalent estimate, next action. Stale and unknown-cleanup states change the second line, never stay green.' }]).filter((b) => b.text || b.caption), { width: 1000 });
await terminal('tui-evidence-panel.png', [
  { title: 'Pi /forge-demo detail · evidence list · 60 columns · selection on Tests', text: renderEvidenceList(snap('head-drift'), 1, { width: 60, color: true, presentation: 'native' }).concat(['j/k or arrows: select | Enter: detail | Esc: close']).join('\n') },
  { title: 'Enter · evidence detail · full head and tree hard-wrapped, never truncated', text: renderEvidenceDetail(snap('head-drift'), 1, { width: 60, color: true, presentation: 'native' }).concat(['j/k: scroll | Esc: evidence list']).join('\n'), caption: '<b>Keyboard evidence drilldown.</b> The selected row is both inverse video and prefixed with &gt;. Detail shows the receipt head versus the run head so stale is explained, not just labelled.' }], { width: 900 });
await terminal('tui-usage-panel-80.png', [{ title: 'node scripts/demo.mjs failed-gate 80 --view usage-panel --color', text: renderUsagePanel(snap('failed-gate'), { width: 80, color: true, presentation: 'harness' }).join('\n'), caption: '<b>Usage panel.</b> Known subtotal, PARTIAL status, per-model share of the known subtotal, cache and reasoning breakdown, request attempts with retries as new incurrences, one unpriced request shown as UNPRICED rather than zero.' }]);
await terminal('tui-ascii-fallback-80.png', [{ title: 'NO_COLOR=1 · --ascii · 80 columns', text: renderCockpit(snap('failed-gate'), { width: 80, ascii: true, color: false, presentation: 'harness' }), caption: '<b>ASCII fallback.</b> Every glyph and box character has a 1:1 ASCII replacement, so widths and alignment are unchanged on terminals without Unicode or colour.' }]);

// contact sheet: eight walkthrough states, compact cockpit at 72 columns
{
  const page = await browser.newPage({ viewport: { width: 1700, height: 1200 }, deviceScaleFactor: 1.5 });
  const cards = snapshotsFromCatalog().slice(0, 8).map(({ name, snapshot }, i) => `<article><h2>${String(i + 1).padStart(2, '0')} ${esc(name)}</h2><pre>${ansiToHtml(renderCockpit(snapshot, { width: 72, color: true, presentation: 'harness' }))}</pre></article>`).join('');
  await page.setContent(`<style>body{margin:0;padding:28px;background:#0b1218;color:#E8EEF2;font:14px system-ui}h1{margin:0 0 4px;font-size:20px}p{color:#AFBEC9;margin:0 0 18px}section{display:grid;grid-template-columns:1fr 1fr;gap:16px}article{background:#101820;border:1px solid #30414D;border-radius:10px;padding:12px 14px;overflow:hidden}h2{margin:0 0 8px;font:700 12px ui-monospace,monospace;color:#F2BA69;letter-spacing:.08em}pre{margin:0;font:10.5px/1.38 "Cascadia Mono",Consolas,ui-monospace,monospace;white-space:pre}</style><h1>Forge UX-2 walkthrough · cockpit at 72 columns</h1><p>HARNESS RENDER · eight shared synthetic states rendered by the same projection · not installed host output</p><section>${cards}</section>`);
  await page.screenshot({ path: path.join(out, 'tui-walkthrough-contact-sheet.png'), fullPage: true }); await page.close();
  written.push({ file: 'tui-walkthrough-contact-sheet.png', kind: 'terminal-simulation', blocks: ['8 walkthrough states'] });
}

// desktop mockups
async function desktop(file, page, params, name, { width = 1440, height = 960, fullPage = false } = {}) {
  const p = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1.5 });
  const errors = []; p.on('pageerror', (e) => errors.push(String(e)));
  await p.goto(`${pathToFileURL(path.join(root, 'desktop', page)).href}?${params}`);
  await p.waitForSelector('.forge-rail');
  await p.screenshot({ path: path.join(out, file), fullPage }); await p.close();
  if (errors.length) throw new Error(`${file}: ${errors.join('; ')}`);
  written.push({ file, kind: 'desktop-mockup', page, params, name });
}
await desktop('desktop-claude-failed-gate.png', 'claude-desktop.html', 'scenario=failed-gate&detail', 'Claude Code Desktop · failed gate · Forge pane overview with evidence detail');
await desktop('desktop-claude-usage.png', 'claude-desktop.html', 'scenario=failed-gate&tab=usage', 'Claude Code Desktop · usage tab');
await desktop('desktop-claude-head-drift.png', 'claude-desktop.html', 'scenario=head-drift&tab=evidence', 'Claude Code Desktop · head drift · evidence tab');
await desktop('desktop-codex-failed-gate.png', 'codex-desktop.html', 'scenario=failed-gate&preview', 'Codex Desktop · failed gate · inline card + thread tab with intent preview open');
await desktop('desktop-codex-pr-receipt.png', 'codex-desktop.html', 'scenario=pr-receipt', 'Codex Desktop · PR receipt');
await desktop('desktop-codex-cancel-unknown.png', 'codex-desktop.html', 'scenario=cancel-unknown', 'Codex Desktop · stop requested, cleanup unknown');
await desktop('desktop-claude-plan.png', 'claude-desktop.html', 'scenario=plan&tab=plan', 'Claude Code Desktop · plan tab at PLAN');
await desktop('panel-failed-gate-overview.png', 'forge-panel.html', 'scenario=failed-gate&detail&preview', 'Shared Forge panel · failed gate · overview with evidence detail and intent preview', { width: 700, height: 900, fullPage: true });
await desktop('panel-review-overview.png', 'forge-panel.html', 'scenario=review', 'Shared Forge panel · review on exact head', { width: 700, height: 900, fullPage: true });
await desktop('panel-cancel-unknown.png', 'forge-panel.html', 'scenario=cancel-unknown', 'Shared Forge panel · stop requested, cleanup unknown', { width: 700, height: 900, fullPage: true });
await desktop('panel-usage.png', 'forge-panel.html', 'scenario=failed-gate&tab=usage', 'Shared Forge panel · usage tab', { width: 700, height: 900, fullPage: true });
await browser.close();

const manifest = { generatedAt: new Date().toISOString(), generator: 'scripts/screenshots-ux2.mjs', runtime: process.version, note: 'Terminal images are deterministic renderer output converted from ANSI to HTML and photographed in Chromium; desktop images are static HTML mockups of host chrome around the shared Forge panel. None is an installed-host capture.', images: written.map((w) => ({ ...w, sha256: crypto.createHash('sha256').update(fs.readFileSync(path.join(out, w.file))).digest('hex') })) };
fs.writeFileSync(path.join(out, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
console.log(`${written.length} images written to ${out}`);
