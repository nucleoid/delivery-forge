#!/usr/bin/env node
import { getScenario } from '../src/fixtures.mjs';
import { renderCompact, renderExpanded, renderUsageDetail } from '../src/render-text.mjs';

const args=process.argv.slice(2),name=args.shift()||'failed-gate';
const widthArg=args[0]&&!args[0].startsWith('--')?args.shift():80;
const width=Number(widthArg)||80;
let presentation='harness',view='expanded';
while(args.length){
  const option=args.shift();
  if(option==='--compact') view='compact';
  else if(option==='--view'){
    const value=args.shift();
    if(!['expanded','compact','usage'].includes(value)) throw new Error('--view must be expanded, compact, or usage');
    view=value;
  }
  else if(option==='--presentation'){
    const value=args.shift();
    if(!['harness','native'].includes(value)) throw new Error('--presentation must be harness or native');
    presentation=value;
  }else throw new Error(`unsupported option: ${option}`);
}
const snapshot=getScenario(name).snapshot;
const output=view==='compact'?renderCompact(snapshot,{width,presentation}).join('\n'):view==='usage'?renderUsageDetail(snapshot,{width,presentation}):renderExpanded(snapshot,{width,presentation});
process.stdout.write(`${output}\n`);
