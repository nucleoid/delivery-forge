#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { getScenario } from '../src/fixtures.mjs';
import { createDetailComponent } from '../hosts/pi/forge-demo-extension.js';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const evidence=path.resolve(process.env.FORGE_EVIDENCE_DIR || path.join(root,'../../docs/ux/native-presentations/evidence'));
fs.mkdirSync(evidence,{recursive:true});
const captures=[['terminal-80','failed-gate','80'],['terminal-120','failed-gate','120'],['failure','tool-fail','100'],['repair','repair','100'],['review','review','100'],['stale','stale-reconnected','100'],['head-drift','head-drift','100'],['cancel-unknown','cancel-unknown','100']];
for(const [label,scenario,width] of captures){
  const result=spawnSync(process.execPath,[path.join(root,'scripts/demo.mjs'),scenario,width],{encoding:'utf8',env:{...process.env,NO_COLOR:'1'},timeout:5000,maxBuffer:262144});
  if(result.status!==0) throw new Error(`${label} capture failed: ${result.stderr}`);
  fs.writeFileSync(path.join(evidence,`${label}.txt`),result.stdout);
  fs.writeFileSync(path.join(evidence,`${label}.ansi`),result.stdout);
}
const mcp=spawnSync(process.execPath,[path.join(root,'hosts/codex/protocol-harness.mjs'),'failed-gate','80'],{encoding:'utf8',timeout:5000,maxBuffer:262144});
if(mcp.status!==0)throw new Error(`MCP harness failed: ${mcp.stderr}`);
fs.writeFileSync(path.join(evidence,'codex-mcp-protocol.txt'),mcp.stdout);
const claude=spawnSync(process.execPath,[path.join(root,'hosts/claude/statusline.mjs'),'--state',path.join(root,'hosts/claude/statusline-state.json')],{input:'{"session_id":"fixture-host-input"}',encoding:'utf8',env:{...process.env,COLUMNS:'80',NO_COLOR:'1',FORGE_PRESENTATION:'harness'},timeout:5000});
if(claude.status!==0)throw new Error(`Claude harness failed: ${claude.stderr}`);
fs.writeFileSync(path.join(evidence,'claude-statusline-80.txt'),`HARNESS RENDER — statusLine contract, not installed Claude Code\n${claude.stdout}\n`);
const keys={matches:(data,key)=>({j:'tui.select.down',k:'tui.select.up','\r':'tui.select.confirm','\x1b':'tui.select.cancel'}[data]===key)};
const piDetail=createDetailComponent(getScenario('failed-gate').snapshot,{tui:{requestRender(){}},keybindings:keys,done(){},presentation:'harness'});
fs.writeFileSync(path.join(evidence,'pi-detail-list-52.txt'),`${piDetail.render(52).join('\n')}\n`);
piDetail.handleInput('j');
fs.writeFileSync(path.join(evidence,'pi-detail-selected-52.txt'),`${piDetail.render(52).join('\n')}\n`);
piDetail.handleInput('\r');
fs.writeFileSync(path.join(evidence,'pi-evidence-detail-52.txt'),`${piDetail.render(52).join('\n')}\n`);
const hash=(file)=>crypto.createHash('sha256').update(fs.readFileSync(path.join(evidence,file))).digest('hex');
const outputs=fs.readdirSync(evidence).filter(x=>/\.(txt|ansi)$/.test(x)).sort();
const provenance={generatedAt:new Date().toISOString(),generator:'scripts/capture-evidence.mjs',runtime:process.version,commands:captures.map(([label,scenario,width])=>({output:`${label}.txt/.ansi`,argv:['node','scripts/demo.mjs',scenario,width],exitCode:0})).concat([{output:'codex-mcp-protocol.txt',argv:['node','hosts/codex/protocol-harness.mjs','failed-gate','80'],exitCode:mcp.status},{output:'claude-statusline-80.txt',argv:['node','hosts/claude/statusline.mjs','--state','hosts/claude/statusline-state.json'],stdin:'bounded synthetic host JSON with FORGE_PRESENTATION=harness',exitCode:claude.status},{output:'pi-detail-list-52.txt / pi-detail-selected-52.txt / pi-evidence-detail-52.txt',argv:['in-process','createDetailComponent','failed-gate','width=52','presentation=harness'],exitCode:0}]),sha256:Object.fromEntries(outputs.map(file=>[file,hash(file)])),note:'All captures are deterministic local harness output from shared synthetic fixtures. No installed coding host was launched.'};
fs.writeFileSync(path.join(evidence,'capture-provenance.json'),JSON.stringify(provenance,null,2)+'\n');
