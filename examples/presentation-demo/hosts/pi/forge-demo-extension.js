import { snapshotsFromCatalog } from '../../src/fixtures.mjs';
import { cockpitView, COCKPIT_VIEWS, linesForView } from '../../src/cockpit-view-model.mjs';
import { renderCockpit, renderStatusLine } from '../../src/render-cockpit.mjs';
import { displayWidth, sliceByColumns, stripAnsi, truncateToWidth } from '../../src/safety.mjs';

const ENTRY='forge-demo-state-v2',LEGACY_ENTRY='forge-demo-state-v1',WIDGET='forge-demo-widget',STATUS='forge-demo-status';
const HINTS='/forge-demo cockpit · view overview|evidence|usage|activity · scenario <name> · next · prev · reset';
const states=snapshotsFromCatalog();
const useColor=(theme)=>Boolean(theme&&typeof theme.fg==='function')&&process.env.NO_COLOR!=='1';
const styled=(theme,token,value,bold=false)=>{if(!useColor(theme))return value;try{const text=bold&&typeof theme.bold==='function'?theme.bold(value):value;return theme.fg(token,text);}catch{return value;}};
const semantic=(theme,value)=>/FAIL|ERROR/.test(value)?styled(theme,'error',value,true):/INCOMPLETE|UNKNOWN|STALE|UNPRICED|PARTIAL/.test(value)?styled(theme,'warning',value,true):/PASS|CURRENT|COMPLETE/.test(value)?styled(theme,'success',value):value;
// Preserve ESC only for renderer-owned SGR sequences; fixture text is sanitised by the projector.
const clean=(value,max=4096)=>String(value??'').replace(/[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f\x7f-\x9f]/g,' ').slice(0,max);
const clip=(line,width)=>displayWidth(line)<=Math.max(1,width)?line:truncateToWidth(stripAnsi(line),Math.max(1,width),'…');
const wrap=(value,width)=>{const W=Math.max(1,width),words=clean(stripAnsi(value)).split(/\s+/),out=[];let line='';for(const word of words){if(!word)continue;let rest=word;while(displayWidth(rest)>W){if(line){out.push(line);line='';}const part=sliceByColumns(rest,W);out.push(part);rest=rest.slice(part.length);}if(!rest)continue;const next=line?`${line} ${rest}`:rest;if(displayWidth(next)>W){if(line)out.push(line);line=rest;}else line=next;}if(line||!out.length)out.push(line);return out;};
const validView=(value)=>COCKPIT_VIEWS.includes(value)?value:'overview';

export function restoreFromActiveBranch(ctx){
  const branch=ctx.sessionManager.getBranch(),entries=Array.isArray(branch)?branch:[];
  for(let i=entries.length-1;i>=0;i--){const item=entries[i];if([ENTRY,LEGACY_ENTRY].includes(item?.customType)&&item.data&&Number.isInteger(item.data.cursor)&&item.data.cursor>=0&&item.data.cursor<states.length)return{cursor:item.data.cursor,scenario:states[item.data.cursor].name,view:validView(item.data.view),selected:Number.isInteger(item.data.selected)?Math.max(0,item.data.selected):0};}
  return{cursor:0,scenario:states[0].name,view:'overview',selected:0};
}

const key=(keybindings,data,name,fallback)=>keybindings?.matches?.(data,name)||data===fallback;
const rowBudget=(tui)=>Math.max(1,Math.min(40,(Number(tui?.terminal?.rows)||24)-5));

export function createCockpitComponent(initialSnapshot,{tui,theme,keybindings,done,history=states,initialScenario='fixture',initialView='overview',initialSelected=0,onState=()=>{},onScenarioChange=()=>null}){
  let snapshot=initialSnapshot,activityHistory=history,scenarioName=initialScenario,view=validView(initialView),selected=Math.max(0,initialSelected),detail=false,search=false,query='',scroll=0,help=false;
  const request=()=>tui.requestRender();
  const setView=(next)=>{view=validView(next);detail=false;search=false;scroll=0;help=false;onState({view,selected});request();};
  const evidence=()=>cockpitView(snapshot,{selected,history:activityHistory}).evidence.filter((x)=>!query||x.search.includes(query.toLocaleLowerCase()));
  const selectedIndex=()=>{const rows=evidence();if(!rows.length)return 0;const id=cockpitView(snapshot,{selected,history:activityHistory}).evidence[selected]?.id;const index=rows.findIndex((x)=>x.id===id);return index<0?0:index;};
  const selectFiltered=(index)=>{const rows=evidence(),item=rows[Math.max(0,Math.min(rows.length-1,index))];if(item){const all=cockpitView(snapshot,{history:activityHistory}).evidence;selected=Math.max(0,all.findIndex((x)=>x.id===item.id));onState({view,selected});}};
  const changeScenario=(delta)=>{const next=onScenarioChange(delta);if(next?.snapshot){snapshot=next.snapshot;activityHistory=next.history||activityHistory;scenarioName=next.name||scenarioName;selected=0;detail=false;search=false;query='';scroll=0;onState({view,selected});request();}};
  return{
    render(width){
      const W=Math.max(1,Number(width)||80),rows=rowBudget(tui),vm=cockpitView(snapshot,{selected,history:activityHistory}),out=[];
      out.push(styled(theme,'accent','FORGE COCKPIT',true),styled(theme,'warning',`SYNTHETIC · READ ONLY · fixture ${scenarioName}`,true),semantic(theme,vm.header[1]),semantic(theme,vm.header[2]),vm.header[3],styled(theme,'muted',vm.header[4]));
      out.push(`${view==='overview'?styled(theme,'accent','[1 Overview]',true):'[1 Overview]'} ${view==='evidence'?styled(theme,'accent','[2 Evidence]',true):'[2 Evidence]'} ${view==='usage'?styled(theme,'accent','[3 Usage]',true):'[3 Usage]'} ${view==='activity'?styled(theme,'accent','[4 Activity]',true):'[4 Activity]'}`,'');
      if(help)out.push(styled(theme,'accent','HELP',true),'1–4 views · n/p fixtures · j/k or arrows move · Enter opens','/ searches Evidence · Esc back/close · ? toggles help');
      else if(view==='evidence'){
        const filtered=evidence();
        out.push(styled(theme,'accent','── EVIDENCE RECEIPTS ──',true));
        if(search)out.push(styled(theme,'warning',`Search evidence: ${query || ' '}`,true));
        if(W>=100){const left=Math.min(46,Math.floor(W*.44)),right=W-left-3;const details=filtered.length?cockpitView(snapshot,{selected:cockpitView(snapshot,{history:activityHistory}).evidence.findIndex((x)=>x.id===filtered[selectedIndex()]?.id),history:activityHistory}).detail:['No evidence matches the active filter.'];const list=filtered.length?filtered.map((x,i)=>`${i===selectedIndex()?'>':' '} ${x.text}`):['  No matching evidence'];const n=Math.max(list.length,details.length);for(let i=0;i<n;i++)out.push(semantic(theme,`${truncateToWidth(list[i]||'',left).padEnd(left)} │ ${truncateToWidth(details[i]||'',right)}`));}
        else if(detail)out.push(styled(theme,'accent','< Back to Evidence',true),'',...vm.detail.map((line)=>semantic(theme,line)));
        else out.push(...(filtered.length?filtered.map((x,i)=>semantic(theme,`${i===selectedIndex()?'>':' '} ${x.text}`)):['No matching evidence']));
      }else{
        out.push(styled(theme,'accent',`── ${view.toUpperCase()} ──`,true));
        for(const line of linesForView(snapshot,view,{selected,history:activityHistory}))out.push(...wrap(line,W).map((part)=>semantic(theme,part)));
      }
      const footer=search?'Type to filter · Backspace edits · Esc clears search':detail?'j/k scroll · Esc evidence':'n/p fixtures · j/k scroll · Enter select · / search · Esc close · ? help';
      const bodyRows=Math.max(0,rows-1),max=Math.max(0,out.length-bodyRows);scroll=Math.max(0,Math.min(scroll,max));
      if(view==='evidence'&&!detail&&!search){const marker=out.findIndex((x)=>x.startsWith('>'));if(marker>=scroll+bodyRows)scroll=marker-bodyRows+1;else if(marker>=0&&marker<scroll)scroll=marker;}
      return rows===1?[clip(footer,W)]:[...out.slice(scroll,scroll+bodyRows),footer].map((x)=>clip(x,W));
    },
    handleInput(data){
      if(search){if(key(keybindings,data,'tui.select.cancel','\x1b')){search=false;query='';scroll=0;request();return;}if(key(keybindings,data,'tui.select.confirm','\r')){search=false;scroll=0;request();return;}if(data==='\x7f'||data==='\b')query=Array.from(query).slice(0,-1).join('');else if(typeof data==='string'&&data&&!/[\x00-\x1f\x7f]/.test(data))query=clean(query+data,64);selectFiltered(0);scroll=0;request();return;}
      if(data>='1'&&data<='4'){setView(COCKPIT_VIEWS[Number(data)-1]);return;}
      if(data==='n'){changeScenario(1);return;}if(data==='p'){changeScenario(-1);return;}
      if(data==='?'){help=!help;scroll=0;request();return;}
      if(data==='/'&&view==='evidence'&&!detail){search=true;query='';scroll=0;request();return;}
      const up=key(keybindings,data,'tui.select.up','k'),down=key(keybindings,data,'tui.select.down','j');
      if(up){if(view==='evidence'&&!detail)selectFiltered(selectedIndex()-1);else scroll=Math.max(0,scroll-1);request();return;}
      if(down){if(view==='evidence'&&!detail)selectFiltered(selectedIndex()+1);else scroll++;request();return;}
      if(key(keybindings,data,'tui.select.confirm','\r')){if(view==='evidence'&&evidence().length){detail=true;scroll=0;request();}return;}
      if(key(keybindings,data,'tui.select.cancel','\x1b')){if(help){help=false;request();}else if(detail){detail=false;scroll=0;request();}else done(undefined);}
    },invalidate(){},dispose(){query='';search=false;detail=false;}
  };
}

export function createDetailComponent(snapshot,options){return createCockpitComponent(snapshot,{...options,initialView:'evidence'});}
export function createUsageComponent(snapshot,options){return createCockpitComponent(snapshot,{...options,initialView:'usage'});}

export default function forgeDemo(pi){
  let state={cursor:0,scenario:states[0].name,view:'overview',selected:0},activeCtx=null,activeDone=null;
  const current=()=>states[state.cursor].snapshot;
  const persist=()=>pi.appendEntry(ENTRY,{cursor:state.cursor,scenario:state.scenario,view:state.view,selected:state.selected});
  const fallback=(view=state.view)=>pi.sendMessage({customType:'forge-demo-output',content:['DEMO • SYNTHETIC',...cockpitView(current(),{selected:state.selected,history:states.slice(0,state.cursor+1)}).header,...linesForView(current(),view,{selected:state.selected,history:states.slice(0,state.cursor+1)})].join('\n'),display:true,details:{demoOnly:true,scenario:state.scenario,view,sideEffects:0,authorityIssued:false}},{triggerTurn:false});
  const clear=()=>{if(activeCtx?.mode==='tui'){activeCtx.ui.setWidget(WIDGET,undefined);activeCtx.ui.setStatus(STATUS,undefined);}if(activeDone){activeDone(undefined);activeDone=null;}};
  const paint=(ctx)=>{activeCtx=ctx;if(ctx.mode!=='tui')return;const snapshot=current();ctx.ui.setStatus(STATUS,`FORGE SYNTHETIC ${snapshot.run.id} ${snapshot.workflow.phase}`);ctx.ui.setWidget(WIDGET,(_tui,theme)=>({render(width){return renderStatusLine(snapshot,{width,presentation:'native',color:useColor(theme)}).slice(0,4).map((x)=>clip(clean(x),width));},invalidate(){}}),{placement:'aboveEditor'});};
  const move=(delta,ctx)=>{state.cursor=Math.max(0,Math.min(states.length-1,state.cursor+delta));state.scenario=states[state.cursor].name;state.selected=0;persist();paint(ctx);};
  const open=async(ctx,view=state.view)=>{state.view=validView(view);if(ctx.mode!=='tui'){fallback(state.view);return;}await ctx.ui.custom((tui,theme,keybindings,done)=>{activeDone=done;return createCockpitComponent(current(),{tui,theme,keybindings,done:(value)=>{activeDone=null;done(value);},history:states.slice(0,state.cursor+1),initialScenario:state.scenario,initialView:state.view,initialSelected:state.selected,onState:(next)=>{state={...state,...next};persist();},onScenarioChange:(delta)=>{move(delta,ctx);return{snapshot:current(),history:states.slice(0,state.cursor+1),name:state.scenario};}});});};
  pi.registerCommand('forge-demo',{description:'Inspect the synthetic Forge cockpit; no model turn or live side effects',handler:async(args,ctx)=>{
    const parts=clean(args).trim().split(/\s+/).filter(Boolean),cmd=parts[0]||'cockpit';
    if(cmd==='next')move(1,ctx);else if(cmd==='prev')move(-1,ctx);else if(cmd==='reset'){state={cursor:0,scenario:states[0].name,view:'overview',selected:0};persist();paint(ctx);}
    else if(cmd==='scenario'){const index=states.findIndex((x)=>x.name===parts[1]);if(index<0){if(ctx.hasUI)ctx.ui.notify(`Unknown fixture scenario: ${clean(parts[1])}`,'error');return;}state={cursor:index,scenario:states[index].name,view:'overview',selected:0};persist();paint(ctx);}
    else if(cmd==='view'){if(!COCKPIT_VIEWS.includes(parts[1])){if(ctx.hasUI)ctx.ui.notify('View must be overview, evidence, usage, or activity','error');return;}await open(ctx,parts[1]);}
    else if(cmd==='detail')await open(ctx,'evidence');else if(cmd==='usage')await open(ctx,'usage');else if(cmd==='cockpit'||cmd==='status')await open(ctx,state.view);else if(ctx.hasUI)ctx.ui.notify(`Unknown forge-demo command: ${cmd}`,'error');
  }});
  pi.on('session_start',(_event,ctx)=>{clear();activeCtx=ctx;state=restoreFromActiveBranch(ctx);paint(ctx);});
  pi.on('session_shutdown',()=>{clear();activeCtx=null;});
}
