#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';
import { snapshotsFromCatalog } from '../src/fixtures.mjs';
import { renderExpanded } from '../src/render-text.mjs';
import { chromium } from 'playwright';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const evidence=path.resolve(process.env.FORGE_EVIDENCE_DIR || path.join(root,'../../docs/ux/native-presentations/evidence'));
const browser=await chromium.launch({headless:true});
const colors={bg:'#101820',panel:'#17232D',line:'#30414D',text:'#E8EEF2',muted:'#AFBEC9',amber:'#F2BA69'};
async function terminalShot(input,name,viewport={width:1100,height:900}){
  const page=await browser.newPage({viewportSize:viewport});
  await page.setContent(`<style>html,body{margin:0;background:${colors.bg};color:${colors.text}}main{margin:24px;border:1px solid ${colors.line};border-radius:10px;background:${colors.panel};padding:22px;display:inline-block;min-width:calc(100% - 92px)}header{color:${colors.amber};font:700 12px system-ui;letter-spacing:.08em;margin-bottom:16px}pre{margin:0;white-space:pre-wrap;color:${colors.text};font:14px/1.45 ui-monospace,SFMono-Regular,Consolas,monospace}</style><main><header>HARNESS RENDER · DETERMINISTIC RAW TERMINAL CAPTURE · NOT INSTALLED HOST</header><pre></pre></main>`);
  await page.locator('pre').evaluate((node,text)=>node.textContent=text,input);
  await page.screenshot({path:path.join(evidence,name),fullPage:true});await page.close();
}
for(const [file,image,width] of [['terminal-80.txt','terminal-80.png',1100],['terminal-120.txt','terminal-120.png',1500],['failure.txt','failure.png',1100],['repair.txt','repair.png',1100],['review.txt','review.png',1100],['codex-mcp-protocol.txt','codex-mcp-protocol.png',1100],['codex-usage-detail-80.txt','codex-usage-detail-80.png',1100],['claude-statusline-80.txt','claude-statusline-80.png',1100],['pi-detail-list-52.txt','pi-detail-list-52.png',900],['pi-detail-selected-52.txt','pi-detail-selected-52.png',900],['pi-evidence-detail-52.txt','pi-evidence-detail-52.png',900],['pi-usage-detail-80.txt','pi-usage-detail-80.png',1100]]) await terminalShot(fs.readFileSync(path.join(evidence,file),'utf8'),image,{width,height:900});

const page=await browser.newPage({viewportSize:{width:1440,height:1100}});
await page.goto(pathToFileURL(path.join(root,'companion/index.html')).href);
await page.locator('#scenario-nav button').nth(3).click();
await page.screenshot({path:path.join(evidence,'companion-desktop.png'),fullPage:true});
await page.setViewportSize({width:390,height:844});
await page.screenshot({path:path.join(evidence,'companion-mobile-390.png'),fullPage:true});
await page.close();

const cards=snapshotsFromCatalog().slice(0,8).map(({name,snapshot},i)=>`<article><b>${String(i+1).padStart(2,'0')} ${name}</b><pre></pre></article>`).join('');
const contact=await browser.newPage({viewportSize:{width:1600,height:1200}});
await contact.setContent(`<style>body{margin:0;padding:28px;background:${colors.bg};color:${colors.text};font:14px system-ui}h1{margin:0 0 8px}p{color:${colors.muted}}section{display:grid;grid-template-columns:1fr 1fr;gap:14px}article{background:${colors.panel};border:1px solid ${colors.line};border-radius:10px;padding:16px;overflow:hidden}b{color:${colors.amber}}pre{font:11px/1.35 ui-monospace,monospace;white-space:pre-wrap}</style><h1>Forge UX-1 walkthrough</h1><p>HARNESS RENDER · eight shared synthetic states · not installed host output</p><section>${cards}</section>`);
const texts=snapshotsFromCatalog().slice(0,8).map(({snapshot})=>renderExpanded(snapshot,{width:72}));
await contact.locator('pre').evaluateAll((nodes,values)=>nodes.forEach((n,i)=>n.textContent=values[i]),texts);
await contact.screenshot({path:path.join(evidence,'walkthrough-contact-sheet.png'),fullPage:true});await contact.close();
await browser.close();
