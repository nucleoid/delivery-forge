import { snapshotsFromCatalog } from '../../src/fixtures.mjs';
import { renderCockpit, renderEvidenceDetail, renderEvidenceList, renderStatusLine, renderUsagePanel } from '../../src/render-cockpit.mjs';
import { displayWidth, stripAnsi } from '../../src/safety.mjs';

const ENTRY='forge-demo-state-v1',WIDGET='forge-demo-widget',STATUS='forge-demo-status',MAX_PANEL_ROWS=18;
const HINTS='/forge-demo detail · usage · next · prev · reset';
const states=snapshotsFromCatalog();
// ANSI colour only when the host hands us a real theme; test doubles and NO_COLOR stay plain
const useColor=(theme)=>Boolean(theme&&typeof theme.fg==='function')&&process.env.NO_COLOR!=='1';
// strip control characters but keep ESC (0x1b): renderer output carries SGR colour sequences, fixture text was already cleaned by the projector
const clean=(value,max=4096)=>String(value??'').replace(/[\x00-\x08\x0b\x0c\x0e-\x1a\x1c-\x1f\x7f]/g,' ').slice(0,max);
const clip=(line,width)=>displayWidth(line)<=Math.max(1,width)?line:[...stripAnsi(line)].slice(0,Math.max(1,width)).join('');

export function restoreFromActiveBranch(ctx){
  const branch=ctx.sessionManager.getBranch(),entries=Array.isArray(branch)?branch:[];
  for(let i=entries.length-1;i>=0;i--){const item=entries[i];if(item?.customType===ENTRY&&item.data&&Number.isInteger(item.data.cursor)&&item.data.cursor>=0&&item.data.cursor<states.length)return{cursor:item.data.cursor,scenario:states[item.data.cursor].name,detail:Boolean(item.data.detail)};}
  return{cursor:0,scenario:states[0].name,detail:false};
}

export function createDetailComponent(snapshot,{tui,theme,keybindings,done,presentation='native'}){
  let selected=0,showDetail=false,scroll=0;
  const request=()=>tui.requestRender();
  const color=useColor(theme);
  return{
    render(width){
      const safeWidth=Math.max(20,Number(width)||80);
      const body=showDetail?renderEvidenceDetail(snapshot,selected,{width:safeWidth,presentation,color}):renderEvidenceList(snapshot,selected,{width:safeWidth,presentation,color});
      const bodyRows=MAX_PANEL_ROWS-1,maxOffset=Math.max(0,body.length-bodyRows);
      if(!showDetail){const marker=body.findIndex(line=>stripAnsi(line).startsWith('>'));if(marker>=0&&marker<scroll)scroll=marker;else if(marker>=scroll+bodyRows)scroll=marker-bodyRows+1;}
      scroll=Math.max(0,Math.min(scroll,maxOffset));
      const help=showDetail?'j/k: scroll | Esc: evidence list':'j/k or arrows: select | Enter: detail | Esc: close';
      return[...body.slice(scroll,scroll+bodyRows),help].map(line=>clip(line,safeWidth));
    },
    handleInput(data){
      const up=keybindings.matches(data,'tui.select.up')||data==='k',down=keybindings.matches(data,'tui.select.down')||data==='j';
      if(up){if(showDetail)scroll=Math.max(0,scroll-1);else selected=Math.max(0,selected-1);request();}
      else if(down){if(showDetail)scroll+=1;else selected=Math.min(Math.max(0,snapshot.gates.length-1),selected+1);request();}
      else if(keybindings.matches(data,'tui.select.confirm')){showDetail=true;scroll=0;request();}
      else if(keybindings.matches(data,'tui.select.cancel')){if(showDetail){showDetail=false;scroll=0;request();}else done(undefined);}
      else if(data==='?'){showDetail=false;scroll=0;request();}
    },
    invalidate(){},
    dispose(){showDetail=false;scroll=0;}
  };
}

export function createUsageComponent(snapshot,{tui,theme,keybindings,done,presentation='native'}){
  let scroll=0;const request=()=>tui.requestRender();const color=useColor(theme);
  return{render(width){const safeWidth=Math.max(20,Number(width)||80),body=renderUsagePanel(snapshot,{width:safeWidth,presentation,color}),rows=MAX_PANEL_ROWS-1,max=Math.max(0,body.length-rows);scroll=Math.max(0,Math.min(scroll,max));return[...body.slice(scroll,scroll+rows),'j/k or arrows: scroll | Esc: close'].map(line=>clip(line,safeWidth));},handleInput(data){const up=keybindings.matches(data,'tui.select.up')||data==='k',down=keybindings.matches(data,'tui.select.down')||data==='j';if(up){scroll=Math.max(0,scroll-1);request();}else if(down){scroll++;request();}else if(keybindings.matches(data,'tui.select.cancel'))done(undefined);},invalidate(){},dispose(){scroll=0;}};
}

export default function forgeDemo(pi){
  let state={cursor:0,scenario:states[0].name,detail:false},activeCtx=null;
  const current=()=>states[state.cursor].snapshot;
  const persist=()=>pi.appendEntry(ENTRY,{cursor:state.cursor,scenario:state.scenario,detail:state.detail});
  const fallback=()=>pi.sendMessage({customType:'forge-demo-output',content:renderCockpit(current(),{width:80,ascii:false,color:false,presentation:'native',hints:HINTS}),display:true,details:{demoOnly:true,scenario:state.scenario,sideEffects:0,authorityIssued:false}},{triggerTurn:false});
  const clear=()=>{if(activeCtx?.mode==='tui'){activeCtx.ui.setWidget(WIDGET,undefined);activeCtx.ui.setStatus(STATUS,undefined);}};
  const paint=(ctx)=>{
    activeCtx=ctx;if(ctx.mode!=='tui')return;const snapshot=current();
    ctx.ui.setStatus(STATUS,`FORGE DEMO ${snapshot.run.id} ${snapshot.workflow.phase}`);
    // persistent 4-line UX-2 status widget above the editor; colour only with a real host theme
    ctx.ui.setWidget(WIDGET,(_tui,theme)=>({render(width){return renderStatusLine(snapshot,{width,presentation:'native',color:useColor(theme)}).map(line=>clip(clean(line),width));},invalidate(){}}),{placement:'aboveEditor'});
  };
  const move=(delta,ctx)=>{state.cursor=Math.max(0,Math.min(states.length-1,state.cursor+delta));state.scenario=states[state.cursor].name;persist();paint(ctx);};
  pi.registerCommand('forge-demo',{description:'Replay synthetic Forge UX-1 fixture states; no model turn or live side effects',handler:async(args,ctx)=>{
    const parts=clean(args).trim().split(/\s+/).filter(Boolean),cmd=parts[0]||'status';
    if(cmd==='next')move(1,ctx);else if(cmd==='prev')move(-1,ctx);else if(cmd==='reset'){state={cursor:0,scenario:states[0].name,detail:false};persist();paint(ctx);}
    else if(cmd==='scenario'){const index=states.findIndex(x=>x.name===parts[1]);if(index<0){if(ctx.hasUI)ctx.ui.notify(`Unknown fixture scenario: ${clean(parts[1])}`,'error');return;}state={cursor:index,scenario:states[index].name,detail:false};persist();paint(ctx);}
    else if(cmd==='detail'){if(ctx.mode!=='tui'){fallback();return;}await ctx.ui.custom((tui,theme,keybindings,done)=>createDetailComponent(current(),{tui,theme,keybindings,done}));}
    else if(cmd==='usage'){if(ctx.mode!=='tui'){pi.sendMessage({customType:'forge-demo-output',content:renderUsagePanel(current(),{width:80,ascii:true,presentation:'native'}).join('\n'),display:true,details:{demoOnly:true,scenario:state.scenario,sideEffects:0,authorityIssued:false}},{triggerTurn:false});return;}await ctx.ui.custom((tui,theme,keybindings,done)=>createUsageComponent(current(),{tui,theme,keybindings,done}));}
    else paint(ctx);if(ctx.mode!=='tui'&&cmd!=='detail')fallback();
  }});
  pi.on('session_start',(_event,ctx)=>{activeCtx=ctx;state=restoreFromActiveBranch(ctx);paint(ctx);});
  pi.on('session_shutdown',()=>{clear();activeCtx=null;});
}
