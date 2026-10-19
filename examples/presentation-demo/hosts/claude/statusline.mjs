#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { getScenario } from '../../src/fixtures.mjs';
import { renderCompact } from '../../src/render-text.mjs';
import { boundLines } from '../../src/safety.mjs';

const here=path.dirname(fileURLToPath(import.meta.url));
const stateArg=process.argv.indexOf('--state');
const stateFile=stateArg>=0?path.resolve(process.argv[stateArg+1]||''):path.join(here,'statusline-state.json');
const width=Math.max(40,Math.min(120,Number(process.env.COLUMNS)||80));
const presentation=process.env.FORGE_PRESENTATION==='harness'?'harness':'native';
const surface=presentation==='native'?'NATIVE DEMO SURFACE | DEMO SYNTHETIC':'HARNESS RENDER | DEMO SYNTHETIC';
const fallback=(reason)=>boundLines([surface,`FORGE DEMO · state unavailable / INCOMPLETE`,`Reconciliation required — ${reason}`,`Detail: /forge-demo:status (synthetic fixture only)`],width).join('\n');
const readPointer=()=>{
  const stat=fs.statSync(stateFile);
  if(stat.size>16384){const error=new Error('state file exceeds 16KiB');error.code='STATE_TOO_LARGE';throw error;}
  return JSON.parse(fs.readFileSync(stateFile,'utf8'));
};
const safeReason=(error)=>{
  if(error?.code==='ENOENT'||error?.code==='ENOTDIR') return 'explicit fixture state is unavailable';
  if(error?.code==='STATE_TOO_LARGE') return 'explicit fixture state exceeds 16KiB';
  if(error instanceof SyntaxError) return 'fixture or host status JSON is malformed';
  return 'explicit fixture state failed validation';
};
let input='';
process.stdin.setEncoding('utf8');
process.stdin.on('data',(chunk)=>{ input+=chunk; if(Buffer.byteLength(input)>65536){ process.stdout.write(fallback('status input exceeds 64KiB')); process.exit(0); }});
process.stdin.on('end',()=>{
  try {
    const host=JSON.parse(input||'{}');
    if (!host || typeof host!=='object' || Array.isArray(host)) throw new Error('invalid JSON object from host');
    const pointer=readPointer();
    if(pointer.schemaVersion!=='forge.statusline-state/0.1'||pointer.mode!=='demo'||typeof pointer.scenario!=='string') throw new Error('invalid explicit state file');
    const {snapshot}=getScenario(pointer.scenario);
    if(snapshot.source.freshness!=='current'||snapshot.source.continuity!=='complete') process.stdout.write(fallback(`state ${snapshot.source.freshness}/${snapshot.source.continuity}; last seq ${snapshot.source.sequence}`));
    else process.stdout.write(renderCompact(snapshot,{width,ascii:process.env.NO_COLOR==='1',presentation}).join('\n'));
  } catch(error) { process.stdout.write(fallback(safeReason(error))); }
});
