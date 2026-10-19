import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { getScenario, loadCatalog, snapshotsFromCatalog } from '../src/fixtures.mjs';
import { aggregate, project, reduceEvents, validateSnapshot } from '../src/projector.mjs';
import { makeIntent, previewIntent, replayAction } from '../src/intents.mjs';
import { displayWidth, htmlEscape, stripAnsi } from '../src/safety.mjs';
import { renderCompact, renderExpanded } from '../src/render-text.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const scenario=(name)=>getScenario(name).snapshot;

test('catalog is strict demo-only and contains eight walkthrough plus recovery variants',()=>{
  const catalog=loadCatalog();
  assert.equal(catalog.demoOnly,true);assert.equal(catalog.events.length,15);
  for(const name of ['launch','plan','execute','failed-gate','repair','review','pr-request','pr-receipt','pause-requested','paused-quiescent','cancel-unknown','stale-reconnected','head-drift','tool-fail','protocol-error']) assert.ok(getScenario(name));
});

test('all snapshots retain full identity and visible demo label',()=>{
  for(const {snapshot} of snapshotsFromCatalog()){
    assert.equal(validateSnapshot(snapshot).length,0);
    assert.equal(snapshot.demoLabel,'DEMO • SYNTHETIC');
    assert.ok(snapshot.run.headCommit.length>30);assert.ok(snapshot.run.planIdentity);assert.equal(snapshot.source.realm,'fixture');
  }
});

test('invalid schema and missing receipt fail validation',()=>{
  const invalid=structuredClone(scenario('failed-gate'));
  invalid.schemaVersion='forge.presentation/999';
  invalid.gates[0].receiptId='';
  assert.match(validateSnapshot(invalid).join(' '),/unsupported snapshot schema/);
  assert.match(validateSnapshot(invalid).join(' '),/missing receipt/);
  assert.equal(aggregate(invalid).outcome,'ERROR');
});

test('incomplete, FAIL, ERROR and head-drift are never fake green',()=>{
  assert.equal(aggregate(scenario('failed-gate')).outcome,'INCOMPLETE');
  assert.equal(aggregate(scenario('repair')).outcome,'INCOMPLETE');
  assert.equal(aggregate(scenario('tool-fail')).outcome,'FAIL');
  assert.equal(aggregate(scenario('protocol-error')).outcome,'ERROR');
  assert.equal(aggregate(scenario('head-drift')).outcome,'INCOMPLETE');
  assert.notEqual(aggregate(scenario('head-drift')).outcome,'PASS');
});

test('failed and superseded attempts remain explicit and cannot aggregate PASS',()=>{
  const failed=scenario('failed-gate'),repair=scenario('repair');
  assert.equal(failed.attempts[0].status,'failed-evidence');
  assert.equal(repair.attempts[0].status,'superseded-incomplete');
  assert.equal(repair.attempts[0].retained,true);
  assert.equal(aggregate(repair).outcome,'INCOMPLETE');
  assert.match(renderExpanded(repair,{width:100}),/superseded-incomplete/);
});

test('proper N/A requires policy allowance and rationale',()=>{
  const good=scenario('failed-gate').gates.find(g=>g.id==='migration');
  assert.equal(good.outcome,'NOT_APPLICABLE');assert.ok(good.notApplicableAllowed);assert.match(good.notApplicableRationale,/No schema/);
  const bad=structuredClone(scenario('failed-gate'));bad.gates.find(g=>g.id==='migration').notApplicableRationale='';
  assert.equal(aggregate(bad).outcome,'ERROR');
});

test('unknown cleanup blocks consequential actions',()=>{
  const p=project(scenario('cancel-unknown'));
  assert.equal(p.workflow.cleanup,'unknown');assert.equal(p.actions.find(a=>a.kind==='intent').available,false);assert.match(p.actions.find(a=>a.kind==='intent').reason,/cleanup unknown/i);
});

test('stale view blocks consequential actions and preserves reads',()=>{
  const p=project(scenario('stale-reconnected'));
  assert.equal(p.actions.find(a=>a.kind==='intent').available,false);assert.equal(p.actions.find(a=>a.kind==='read').available,true);
});

test('event reduction handles exact duplicates, conflicting duplicates, order and gaps',()=>{
  const e=loadCatalog().events[0];
  assert.deepEqual(reduceEvents([e,e],{}).errors,[]);
  const conflict=structuredClone(e);conflict.data.focus={title:'changed'};
  assert.match(reduceEvents([e,conflict],{}).errors.join(' '),/conflicting duplicate/);
  const second=loadCatalog().events[1];
  assert.match(reduceEvents([second,e],{}).errors.join(' '),/gap|out-of-order/);
  assert.equal(reduceEvents([e,loadCatalog().events[2]],{}).continuity,'gap');
});

test('intent preview binds run/plan/revision/head/tree/policy/capability and has zero authority',()=>{
  const s=scenario('execute'),intent=makeIntent(s,'request.pause'),result=previewIntent(s,intent);
  assert.equal(result.status,'PREVIEW_ONLY');assert.equal(result.sideEffects,0);assert.equal(result.authorityIssued,false);
  for(const [field,value] of [['runId','wrong'],['expectedPlanIdentity','wrong'],['expectedPlanRevision',99],['expectedHeadCommit','wrong'],['expectedTreeId','wrong'],['expectedPolicyIdentity','wrong'],['expectedCapabilityIdentity','wrong']]){
    assert.equal(previewIntent(s,{...intent,[field]:value}).status,'REJECTED',field);
  }
});

test('stale and unknown-cleanup intent previews reject',()=>{
  for(const name of ['stale-reconnected','cancel-unknown']){
    const s=scenario(name),intent=makeIntent(s,name==='cancel-unknown'?'request.resume':'request.publish-pr');
    assert.equal(previewIntent(s,intent).status,'REJECTED');
  }
});

test('fixture replay has separate demo namespace',()=>{
  assert.deepEqual(replayAction('demo.next'),{namespace:'demo',action:'demo.next',sideEffects:0,authorityIssued:false});
  assert.throws(()=>replayAction('request.pause'));
});

test('terminal and HTML escaping neutralize controls and markup',()=>{
  const malicious=structuredClone(scenario('launch'));malicious.focus.title='<script>\u001b[31mBAD</script>';
  const rendered=renderExpanded(malicious,{width:80});
  assert.equal(stripAnsi(rendered),rendered);assert.doesNotMatch(rendered,/\u001b/);assert.equal(htmlEscape('<b>&"'), '&lt;b&gt;&amp;&quot;');
});

test('80 and 120-column renderers do not overflow',()=>{
  for(const width of [80,120])for(const {snapshot} of snapshotsFromCatalog())for(const line of renderExpanded(snapshot,{width}).split('\n')) assert.ok(displayWidth(line)<=width,`${width}: ${line}`);
});

test('compact renderer stays within four lines at 80 and carries honest state',()=>{
  for(const name of ['failed-gate','stale-reconnected','cancel-unknown']){
    const lines=renderCompact(scenario(name),{width:80,ascii:true});assert.ok(lines.length<=4);assert.ok(lines.every(x=>displayWidth(x)<=80));
  }
});

test('host packages contain no live/network/model process adapters',()=>{
  const files=[];const walk=(dir)=>{for(const entry of fs.readdirSync(dir,{withFileTypes:true})){const p=path.join(dir,entry.name);entry.isDirectory()?walk(p):files.push(p);}};walk(path.join(root,'hosts'));
  const source=files.filter(f=>/\.(mjs|js)$/.test(f)&&!f.endsWith('protocol-harness.mjs')).map(f=>fs.readFileSync(f,'utf8')).join('\n');
  assert.doesNotMatch(source,/from ['"]node:(child_process|http|https|net|tls)['"]/);
  assert.doesNotMatch(source,/\bfetch\s*\(/);assert.doesNotMatch(source,/\b(codex|claude|pi)\s+(exec|run|chat)\b/i);
});

test('measurements are explicit not-measured values',()=>{
  for(const m of scenario('launch').measurements){assert.equal(m.value,null);assert.equal(m.source,'not measured');}
});

test('policy requirements independently prevent empty or incomplete gate sets from passing',()=>{
  const launch=scenario('launch');
  assert.deepEqual(launch.requirements.gateIds,['build','tests','crap4csharp','mutation','migration']);
  assert.equal(launch.requirements.reviewRequired,true);
  assert.equal(launch.requirements.policyIdentity,launch.run.policyIdentity);
  assert.equal(aggregate(launch).outcome,'INCOMPLETE');
  const missing=structuredClone(scenario('review'));
  missing.gates=missing.gates.filter(g=>g.id!=='mutation');
  assert.equal(aggregate(missing).outcome,'INCOMPLETE');
});

test('required independent review accepts PASS only and rejects invalid outcomes',()=>{
  const incomplete=structuredClone(scenario('review'));incomplete.review.outcome='INCOMPLETE';
  assert.equal(aggregate(incomplete).outcome,'INCOMPLETE');
  const invalid=structuredClone(scenario('review'));invalid.review.outcome='MAYBE';
  assert.equal(aggregate(invalid).outcome,'ERROR');
});

test('duplicate receipt identities and policy identity mismatch are non-PASS',()=>{
  const duplicateGate=structuredClone(scenario('review'));duplicateGate.gates.push(structuredClone(duplicateGate.gates[0]));
  assert.equal(aggregate(duplicateGate).outcome,'ERROR');
  const duplicateReceipt=structuredClone(scenario('review'));duplicateReceipt.gates[1].receiptId=duplicateReceipt.gates[0].receiptId;
  assert.equal(aggregate(duplicateReceipt).outcome,'ERROR');
  const policyMismatch=structuredClone(scenario('review'));policyMismatch.requirements.policyIdentity='other-policy';
  assert.equal(aggregate(policyMismatch).outcome,'ERROR');
});

test('event continuity failures flow through projection and block intent preview',()=>{
  const catalog=loadCatalog(), base=structuredClone(catalog.baseSnapshot), first=catalog.events[0], third=catalog.events[2];
  const reduced=reduceEvents([first,third],base);
  assert.equal(reduced.snapshot.source.continuity,'gap');
  assert.equal(project(reduced.snapshot).actions.find(a=>a.kind==='intent')?.available,false);
  const intent=makeIntent(reduced.snapshot,'request.pause');
  assert.equal(previewIntent(reduced.snapshot,intent).status,'REJECTED');
});

test('event reducer rejects malformed identity, sequence and conflicting duplicates',()=>{
  const catalog=loadCatalog(),base=structuredClone(catalog.baseSnapshot),event=structuredClone(catalog.events[0]);
  const malformed={...event,eventId:'',runId:'other-run',sequence:0};
  const conflict=structuredClone(event);conflict.data.focus={title:'changed'};
  for(const events of [[malformed],[event,conflict],[catalog.events[1],event]]){
    const reduced=reduceEvents(events,base);
    assert.notEqual(reduced.snapshot.source.continuity,'complete');
    assert.notEqual(project(reduced.snapshot).aggregate.outcome,'PASS');
  }
  const inconsistent=structuredClone(event);inconsistent.data.source.sequence=99;
  assert.equal(reduceEvents([inconsistent],base).snapshot.source.continuity,'error');
});

test('public project, render and intent entrypoints fail closed on malformed snapshots',()=>{
  for(const malformed of [null,{}, {schemaVersion:'forge.presentation/0.1',mode:'demo'},{...scenario('review'),gates:[null,{id:4,currentness:7}],integrations:[null],measurements:{},actions:[null]}]){
    const projected=project(malformed);
    assert.equal(projected.aggregate.outcome,'ERROR');
    assert.doesNotThrow(()=>renderExpanded(malformed,{width:80}));
    assert.match(renderExpanded(malformed,{width:80}),/ERROR|INCOMPLETE/);
    assert.equal(previewIntent(malformed,{}).status,'REJECTED');
  }
});

test('presentation provenance is explicit without changing projected facts',()=>{
  const snapshot=scenario('failed-gate');
  const harness=renderExpanded(snapshot,{width:100,presentation:'harness'});
  const native=renderExpanded(snapshot,{width:100,presentation:'native'});
  assert.match(harness,/HARNESS RENDER/);assert.match(native,/NATIVE DEMO SURFACE/);
  assert.doesNotMatch(native,/HARNESS RENDER/);
  assert.match(harness,/EVALUATE \/ INCOMPLETE/);assert.match(native,/EVALUATE \/ INCOMPLETE/);
});
