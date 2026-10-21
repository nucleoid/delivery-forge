import test from 'node:test';
import assert from 'node:assert/strict';
import forgeDemo,{createCockpitComponent,createDetailComponent,createUsageComponent,restoreFromActiveBranch} from '../hosts/pi/forge-demo-extension.js';
import { getScenario } from '../src/fixtures.mjs';

function harness(mode='tui',branch=[]){
  const handlers={},commands={},calls=[],entries=[];
  const ui={setStatus:(...x)=>calls.push(['status',...x]),setWidget:(...x)=>calls.push(['widget',...x]),notify:(...x)=>calls.push(['notify',...x]),custom:async(factory)=>{const component=factory({requestRender:()=>calls.push(['render'])},{},{matches:(data,key)=>({j:'tui.select.down',k:'tui.select.up','\r':'tui.select.confirm','\x1b':'tui.select.cancel'}[data]===key)},()=>calls.push(['done']));calls.push(['custom',component]);}};
  const ctx={mode,hasUI:mode==='tui'||mode==='rpc',ui,sessionManager:{getBranch:()=>branch}};
  const pi={registerCommand:(name,x)=>commands[name]=x,on:(name,fn)=>handlers[name]=fn,appendEntry:(type,data)=>entries.push({customType:type,data}),sendMessage:(message,options)=>calls.push(['message',message,options])};
  forgeDemo(pi);return{handlers,commands,calls,entries,ctx};
}
test('Pi restores only validated state from active branch',()=>{
  assert.equal(restoreFromActiveBranch({sessionManager:{getBranch:()=>[{customType:'forge-demo-state-v1',data:{cursor:3,detail:true}}]}}).cursor,3);
  assert.equal(restoreFromActiveBranch({sessionManager:{getBranch:()=>[{customType:'forge-demo-state-v1',data:{cursor:999}}]}}).cursor,0);
});
test('Pi session_start paints only in tui; RPC receives text without custom TUI or model turn',async()=>{
  const tui=harness('tui');tui.handlers.session_start({},tui.ctx);assert.ok(tui.calls.some(x=>x[0]==='widget'));assert.ok(tui.calls.some(x=>x[0]==='status'));
  const rpc=harness('rpc');rpc.handlers.session_start({},rpc.ctx);assert.equal(rpc.calls.length,0);await rpc.commands['forge-demo'].handler('detail',rpc.ctx);const message=rpc.calls.find(x=>x[0]==='message');assert.ok(message);assert.match(message[1].content,/DEMO • SYNTHETIC/);assert.equal(message[2].triggerTurn,false);assert.ok(!rpc.calls.some(x=>x[0]==='custom'));
});
test('Pi replay commands persist fixture cursor without model turn',async()=>{const h=harness();h.handlers.session_start({},h.ctx);await h.commands['forge-demo'].handler('next',h.ctx);assert.equal(h.entries.at(-1).data.cursor,1);assert.equal(Object.keys(h.commands).length,1);});
test('Pi shutdown clears only owned status and widget',()=>{const h=harness();h.handlers.session_start({},h.ctx);h.handlers.session_shutdown();assert.ok(h.calls.some(x=>x[0]==='widget'&&x[1]==='forge-demo-widget'&&x[2]===undefined));assert.ok(h.calls.some(x=>x[0]==='status'&&x[1]==='forge-demo-status'&&x[2]===undefined));});
test('Pi detail component supports selection, Enter and Escape',()=>{const calls=[],done=[];const keys={matches:(d,k)=>({j:'tui.select.down','\r':'tui.select.confirm','\x1b':'tui.select.cancel'}[d]===k)};const c=createDetailComponent(getScenario('failed-gate').snapshot,{tui:{requestRender:()=>calls.push('render')},keybindings:keys,done:x=>done.push(x)});assert.ok(c.render(80).every(x=>[...x].length<=80));c.handleInput('j');c.handleInput('\r');assert.match(c.render(80).join('\n'),/Tests/);c.handleInput('\x1b');c.handleInput('\x1b');assert.equal(done.length,1);});

test('Pi detail panel is bounded and visibly marks selection before Enter',()=>{const calls=[];const keys={matches:(d,k)=>({j:'tui.select.down',k:'tui.select.up','\r':'tui.select.confirm','\x1b':'tui.select.cancel'}[d]===k)};const c=createDetailComponent(getScenario('failed-gate').snapshot,{tui:{requestRender:()=>calls.push('render')},keybindings:keys,done:()=>{}});const before=c.render(52);assert.ok(before.length<=18);assert.match(before.join('\n'),/>.*Build/);c.handleInput('j');const after=c.render(52);assert.match(after.join('\n'),/>.*Tests/);assert.doesNotMatch(after.join('\n'),/>.*Build/);});

test('Pi component invalidation is cache-only and widget rendering receives host width',()=>{const h=harness();h.handlers.session_start({},h.ctx);const widget=h.calls.find(x=>x[0]==='widget');assert.equal(typeof widget[2],'function');const component=widget[2]({requestRender:()=>h.calls.push(['render'])},{});assert.doesNotThrow(()=>component.invalidate());assert.equal(h.calls.filter(x=>x[0]==='render').length,0);for(const width of [40,120])assert.ok(component.render(width).every(line=>[...line].length<=width));});
test('Pi usage command opens an actual-height keyboard panel and non-TUI emits no model turn',async()=>{const h=harness();h.handlers.session_start({},h.ctx);await h.commands['forge-demo'].handler('scenario failed-gate',h.ctx);await h.commands['forge-demo'].handler('usage',h.ctx);const component=h.calls.findLast(x=>x[0]==='custom')[1];for(const width of [40,80,120])assert.ok(component.render(width).length<=19&&component.render(width).every(line=>[...line].length<=width));assert.match(component.render(80).join('\n'),/USD API-EQUIVALENT/);component.handleInput('j');component.handleInput('\x1b');const rpc=harness('rpc');await rpc.commands['forge-demo'].handler('usage',rpc.ctx);const message=rpc.calls.find(x=>x[0]==='message');assert.equal(message[2].triggerTurn,false);assert.match(message[1].content,/SYNTHETIC TOKENS/);});

test('Pi widget keeps ANSI colour intact when the host supplies a real theme, and stays width-bounded',()=>{const h=harness();h.handlers.session_start({},h.ctx);const widget=h.calls.find(x=>x[0]==='widget');const colored=widget[2]({requestRender(){}},{fg(){return '';}});const lines=colored.render(80);assert.ok(lines.some(line=>line.includes('\x1b[')),'SGR sequences present');for(const line of lines){assert.doesNotMatch(line,/(^|[^\x1b])\[38;2;/,'no literal colour code');assert.ok([...line.replace(/\x1b\[[0-?]*[ -\/]*[@-~]/g,'')].length<=80);}const plain=widget[2]({requestRender(){}},{});assert.ok(plain.render(80).every(line=>!line.includes('\x1b')));});

test('Pi cockpit has four keyboard views, bounded search, retained evidence selection and help',()=>{
  const renders=[],done=[];const keys={matches:(d,k)=>({j:'tui.select.down',k:'tui.select.up','\r':'tui.select.confirm','\x1b':'tui.select.cancel'}[d]===k)};
  const c=createCockpitComponent(getScenario('failed-gate').snapshot,{tui:{terminal:{rows:30,columns:120},requestRender:()=>renders.push(1)},keybindings:keys,done:(x)=>done.push(x)});
  c.handleInput('2');assert.match(c.render(120).join('\n'),/EVIDENCE/);c.handleInput('j');c.handleInput('/');for(const ch of 'tests')c.handleInput(ch);assert.match(c.render(120).join('\n'),/>.*Tests/);c.handleInput('\x1b');c.handleInput('\r');assert.match(c.render(80).join('\n'),/receipt-tests/);c.handleInput('\x1b');assert.match(c.render(80).join('\n'),/>.*Tests/);c.handleInput('?');assert.match(c.render(80).join('\n'),/HELP/);assert.ok(renders.length>0);assert.equal(done.length,0);
});

test('Pi cockpit uses actual terminal height and wide list/detail composition',()=>{
  const keys={matches:()=>false};
  for(const rows of [24,30]){const c=createCockpitComponent(getScenario('failed-gate').snapshot,{tui:{terminal:{rows,columns:120},requestRender(){}},keybindings:keys,done(){},initialView:'evidence'});const out=c.render(120);assert.ok(out.length<=rows-4);assert.match(out.join('\n'),/│/);assert.match(out.join('\n'),/receipt-build/);}
});

test('Pi cockpit uses host theme tokens and cycles fixtures without leaving the native component',()=>{
  const scenarios=[getScenario('failed-gate'),getScenario('repair')];let cursor=0;
  const theme={fg:(token,text)=>`\x1b[3${token==='error'?1:token==='warning'?3:token==='success'?2:6}m${text}\x1b[0m`,bold:(text)=>`\x1b[1m${text}\x1b[22m`};
  const c=createCockpitComponent(scenarios[0].snapshot,{tui:{terminal:{rows:30,columns:120},requestRender(){}},theme,keybindings:{matches:()=>false},done(){},initialScenario:'failed-gate',onScenarioChange:(delta)=>{cursor=Math.max(0,Math.min(scenarios.length-1,cursor+delta));return{snapshot:scenarios[cursor].snapshot,history:scenarios.slice(0,cursor+1),name:scenarios[cursor].name};}});
  assert.match(c.render(120).join('\n'),/\x1b\[/);assert.match(c.render(120).join('\n'),/failed-gate/);
  c.handleInput('n');const repaired=c.render(120).join('\n');assert.match(repaired,/fixture repair/);assert.match(repaired,/EXACT_HEAD_RERUN_REQUIRED/);assert.match(repaired,/SYNTHETIC · READ ONLY/);
});
