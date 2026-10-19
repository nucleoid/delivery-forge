import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const script=path.join(root,'hosts/claude/statusline.mjs'),state=path.join(root,'hosts/claude/statusline-state.json');
const run=(input='{}',file=state,columns='80',presentation)=>spawnSync(process.execPath,[script,'--state',file],{input,encoding:'utf8',env:{...process.env,COLUMNS:columns,NO_COLOR:'1',...(presentation?{FORGE_PRESENTATION:presentation}:{})},timeout:3000});
test('Claude statusline reads host JSON and explicit fixture state',()=>{const r=run('{"session_id":"demo"}');assert.equal(r.status,0);assert.match(r.stdout,/FORGE DEMO/);assert.match(r.stdout,/INCOMPLETE/);assert.ok(r.stdout.split('\n').length<=4);});
test('Claude statusline fails closed for invalid stdin',()=>{const r=run('{');assert.equal(r.status,0);assert.match(r.stdout,/state unavailable \/ INCOMPLETE/);});
test('Claude statusline fails closed for missing state',()=>{const r=run('{}','/definitely/missing/forge-state.json');assert.equal(r.status,0);assert.match(r.stdout,/Reconciliation required/);});
test('Claude statusline marks stale fixture unavailable',()=>{
  const temp=path.join(root,'hosts/claude/statusline-stale.test.json');
  try{fs.writeFileSync(temp,JSON.stringify({schemaVersion:'forge.statusline-state/0.1',mode:'demo',scenario:'stale-reconnected'}));const r=run('{}',temp);assert.match(r.stdout,/stale\/unknown/);}finally{try{fs.unlinkSync(temp);}catch{}}
});
test('Claude statusline respects widths 80 and 120',()=>{for(const w of ['80','120']){const r=run('{}',state,w);assert.ok(r.stdout.split('\n').every(line=>[...line].length<=Number(w)));}});
test('Claude statusline distinguishes native and harness provenance',()=>{assert.match(run('{}').stdout,/NATIVE DEMO SURFACE/);assert.match(run('{}',state,'80','harness').stdout,/HARNESS RENDER/);});
