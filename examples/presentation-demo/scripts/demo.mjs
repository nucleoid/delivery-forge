#!/usr/bin/env node
import { getScenario } from '../src/fixtures.mjs';
import { renderCompact, renderExpanded, renderUsageDetail } from '../src/render-text.mjs';
import { renderCockpit, renderEvidenceDetail, renderEvidenceList, renderStatusLine, renderUsagePanel } from '../src/render-cockpit.mjs';

const VIEWS=['expanded','compact','usage','cockpit','status','evidence','evidence-detail','usage-panel'];
const args=process.argv.slice(2),name=args.shift()||'failed-gate';
const widthArg=args[0]&&!args[0].startsWith('--')?args.shift():80;
const width=Number(widthArg)||80;
let presentation='harness',view='cockpit',color=false,ascii,selected=0;
while(args.length){
  const option=args.shift();
  if(option==='--compact') view='compact';
  else if(option==='--view'){
    const value=args.shift();
    if(!VIEWS.includes(value)) throw new Error(`--view must be one of ${VIEWS.join(', ')}`);
    view=value;
  }
  else if(option==='--presentation'){
    const value=args.shift();
    if(!['harness','native'].includes(value)) throw new Error('--presentation must be harness or native');
    presentation=value;
  }
  else if(option==='--color') color=true;
  else if(option==='--ascii') ascii=true;
  else if(option==='--select'){ selected=Math.max(0,Number(args.shift())||0); }
  else throw new Error(`unsupported option: ${option}`);
}
const snapshot=getScenario(name).snapshot;
const cockpitOptions={width,presentation,color,ascii};
const output=view==='compact'?renderCompact(snapshot,{width,presentation}).join('\n')
  :view==='usage'?renderUsageDetail(snapshot,{width,presentation})
  :view==='cockpit'?renderCockpit(snapshot,cockpitOptions)
  :view==='status'?renderStatusLine(snapshot,cockpitOptions).join('\n')
  :view==='evidence'?renderEvidenceList(snapshot,selected,cockpitOptions).join('\n')
  :view==='evidence-detail'?renderEvidenceDetail(snapshot,selected,cockpitOptions).join('\n')
  :view==='usage-panel'?renderUsagePanel(snapshot,cockpitOptions).join('\n')
  :renderExpanded(snapshot,{width,presentation});
process.stdout.write(`${output}\n`);
