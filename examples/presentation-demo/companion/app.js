(()=>{
  'use strict';
  const states=globalThis.FORGE_DEMO_STATES||[];
  const phases=['Launch','Plan','Execute','Evaluate','Repair','Review','PR'];
  let cursor=Math.max(0,Math.min(states.length-1,Number(sessionStorage.getItem('forge-demo-cursor'))||0));
  let selected=0;
  const $=(id)=>document.getElementById(id);
  const text=(id,value)=>{$(id).textContent=String(value??'');};
  const state=()=>states[cursor];
  const exactAction=()=>state().actions.find(a=>a.id===state().focus.nextActionId)||state().actions.find(a=>a.kind==='intent')||state().actions[0];
  const detail=()=>{
    const g=state().gates[selected];
    text('detail',g?`${g.label}\n${g.outcome} / ${g.currentness}\nreceipt=${g.receiptId}\nhead=${g.headCommit}\n${g.outcome==='NOT_APPLICABLE'?`N/A: ${g.notApplicableRationale}`:(g.limitations.join('; ')||'No recorded limitations')}`:'No gate receipt in this fixture state. Aggregate cannot infer green.');
  };
  const render=()=>{
    const s=state(), action=exactAction();
    sessionStorage.setItem('forge-demo-cursor',String(cursor));
    document.querySelectorAll('#scenario-nav button').forEach((b,i)=>{b.classList.toggle('active',i===cursor);b.setAttribute('aria-current',i===cursor?'step':'false');});
    text('identity',`DEMO • SYNTHETIC / ${s.run.id} / plan ${s.run.planIdentity} r${s.run.planRevision} / head ${s.run.headCommit} / ceiling ${s.run.authorizationCeiling.toUpperCase()} / seq ${s.source.sequence}`);
    $('trail').replaceChildren(...phases.map(p=>{const span=document.createElement('span');span.textContent=p+(p==='PR'?'':' →');if(p.toUpperCase()===s.workflow.phase)span.className='current';return span;}));
    text('phase-outcome',`${s.workflow.phase} / ${s.aggregate.outcome}`);text('focus-title',s.focus.title);text('explanation',s.focus.explanation);
    text('control-state',`Desired ${s.workflow.desiredState} · observed ${s.workflow.observedState} · cleanup ${s.workflow.cleanup} · active owned operations ${s.workflow.activeOperationCount??'UNKNOWN'}`);
    text('next-title',action?.label||'No action admissible');text('next-reason',action?.reason||s.focus.nextActionReason);
    $('preview').disabled=!action||action.kind!=='intent'||!action.available;$('intent').classList.remove('open');text('intent','');
    text('evidence-title',s.aggregate.outcome);
    $('evidence').replaceChildren(...s.gates.map((g,i)=>{const row=document.createElement('div');row.className='gate'+(i===selected?' selected':'');row.tabIndex=0;row.innerHTML=`<strong><span></span><span class="state ${g.outcome}"></span></strong><span class="receipt"></span>`;row.querySelector('strong span').textContent=g.label;row.querySelector('.state').textContent=g.outcome+(g.currentness==='current'?'':` / ${g.currentness.toUpperCase()}`);row.querySelector('.receipt').textContent=g.receiptId;row.onclick=()=>{selected=i;render();};row.onkeydown=e=>{if(e.key==='Enter'){selected=i;detail();}};return row;}));
    selected=Math.min(selected,Math.max(0,s.gates.length-1));detail();
    $('integrations').replaceChildren(...s.integrations.map(x=>{const d=document.createElement('div');d.className='integration';const h=document.createElement('strong'),p=document.createElement('p'),q=document.createElement('p');h.textContent=x.name;p.textContent=`${x.observedCapability} · ${x.version}`;q.textContent=x.limitations.join('; ');d.append(h,p,q);return d;}));
    $('measurements').replaceChildren(...s.measurements.map(m=>{const p=document.createElement('p');p.textContent=`${m.label}: ${m.value===null?'not measured':`${m.value} ${m.unit}`} (${m.source})`;return p;}));
    text('announce',`${s.workflow.phase}. ${s.aggregate.outcome}. ${s.focus.title}`);
  };
  $('scenario-nav').replaceChildren(...states.map((s,i)=>{const b=document.createElement('button');b.type='button';b.textContent=`${String(i+1).padStart(2,'0')} ${s.name}`;b.onclick=()=>{cursor=i;selected=0;render();};return b;}));
  $('preview').onclick=()=>{const s=state(),a=exactAction();const preview={status:'PREVIEW_ONLY',action:a?.id,runId:s.run.id,expectedSnapshotId:s.source.snapshotId,expectedSequence:s.source.sequence,expectedPlanIdentity:s.run.planIdentity,expectedHeadCommit:s.run.headCommit,expectedCapabilityIdentity:s.source.capabilityIdentity,sideEffects:0,authorityIssued:false};text('intent',JSON.stringify(preview,null,2));$('intent').classList.add('open');$('intent').focus();};
  addEventListener('keydown',(e)=>{if(['INPUT','TEXTAREA','BUTTON'].includes(document.activeElement?.tagName))return;if(e.key==='ArrowRight'){cursor=Math.min(states.length-1,cursor+1);selected=0;render();}else if(e.key==='ArrowLeft'){cursor=Math.max(0,cursor-1);selected=0;render();}else if(e.key==='ArrowDown'){selected=Math.min(Math.max(0,state().gates.length-1),selected+1);render();}else if(e.key==='ArrowUp'){selected=Math.max(0,selected-1);render();}else if(e.key==='Enter')detail();else if(e.key.toLowerCase()==='p')$('preview').click();else if(e.key.toLowerCase()==='r'){cursor=0;selected=0;render();}});
  globalThis.ForgeCompanion={get cursor(){return cursor},get selected(){return selected},render};
  render();
})();
