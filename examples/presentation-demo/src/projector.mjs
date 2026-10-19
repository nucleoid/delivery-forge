import { cleanText } from './safety.mjs';
import { usageForScenario } from './usage.mjs';

const OUTCOMES = new Set(['PASS','FAIL','INCOMPLETE','ERROR','NOT_APPLICABLE']);
const REVIEW_OUTCOMES = new Set(['PASS','FAIL','INCOMPLETE','ERROR']);
const CEILINGS = new Set(['plan','implement','pr','merge']);
const PHASES = new Set(['LAUNCH','PLAN','EXECUTE','EVALUATE','REPAIR','REVIEW','PR']);
const CONTINUITY = new Set(['complete','gap','out-of-order','error','unknown']);
const consequential = (action) => action?.kind === 'intent';
const object = (value) => value && typeof value === 'object' && !Array.isArray(value);
const list = (value) => Array.isArray(value) ? value : [];

function safeSnapshot(snapshot) {
  const value=object(snapshot)?snapshot:{};
  const run=object(value.run)?value.run:{},source=object(value.source)?value.source:{};
  const workflow=object(value.workflow)?value.workflow:{},focus=object(value.focus)?value.focus:{};
  const requirements=object(value.requirements)?value.requirements:{};
  const gates=list(value.gates).slice(0,100).map((raw,index)=>{const gate=object(raw)?raw:{};return{id:cleanText(gate.id||`invalid-${index}`,80),label:cleanText(gate.label||gate.id||`Invalid gate ${index+1}`,120),required:gate.required===true,outcome:OUTCOMES.has(gate.outcome)?gate.outcome:'ERROR',currentness:cleanText(gate.currentness||'unknown',40),receiptId:cleanText(gate.receiptId||'',160),headCommit:cleanText(gate.headCommit||'',160),treeId:cleanText(gate.treeId||'',160),policyIdentity:cleanText(gate.policyIdentity||'',160),artifactRefs:list(gate.artifactRefs).map(x=>cleanText(x,240)).slice(0,100),limitations:list(gate.limitations).map(x=>cleanText(x,240)).slice(0,100),notApplicableRationale:gate.notApplicableRationale?cleanText(gate.notApplicableRationale,500):null,notApplicableAllowed:gate.notApplicableAllowed===true};});
  const actions=list(value.actions).slice(0,100).map(raw=>{const action=object(raw)?raw:{};return{id:cleanText(action.id||'',100),label:cleanText(action.label||'Unavailable action',160),kind:action.kind==='intent'?'intent':'read',available:action.available===true,reason:cleanText(action.reason||'No rationale supplied.',500),requiredCapability:action.requiredCapability?cleanText(action.requiredCapability,160):null};});
  const integrations=list(value.integrations).slice(0,100).map(raw=>{const item=object(raw)?raw:{};return{name:cleanText(item.name||'Invalid integration',120),role:cleanText(item.role||'unavailable',240),desired:item.desired===true,observedCapability:cleanText(item.observedCapability||'unavailable',120),version:cleanText(item.version||'not measured',120),evidenceRef:cleanText(item.evidenceRef||'',240),limitations:list(item.limitations).map(x=>cleanText(x,240)).slice(0,100)};});
  const measurements=list(value.measurements).slice(0,100).map(raw=>{const item=object(raw)?raw:{};return{label:cleanText(item.label||'Invalid measurement',120),value:typeof item.value==='number'&&Number.isFinite(item.value)?item.value:null,unit:cleanText(item.unit||'unknown',80),source:cleanText(item.source||'not measured',120),observedAt:item.observedAt?cleanText(item.observedAt,120):null,isEstimate:item.isEstimate===true,scope:cleanText(item.scope||'demo',80)};});
  const attempts=list(value.attempts).slice(0,100).map(raw=>{const item=object(raw)?raw:{};return{id:cleanText(item.id||'invalid-attempt',120),status:cleanText(item.status||'unknown',120),headCommit:cleanText(item.headCommit||'',160),receiptId:cleanText(item.receiptId||'',160),retained:item.retained===true};});
  const rawReview=object(value.review)?value.review:null;
  const review=rawReview?{...rawReview,outcome:REVIEW_OUTCOMES.has(rawReview.outcome)?rawReview.outcome:'ERROR',headCommit:cleanText(rawReview.headCommit||'',160),treeId:cleanText(rawReview.treeId||'',160),patchSha256:cleanText(rawReview.patchSha256||'',160),independence:cleanText(rawReview.independence||'unverified',80),assessmentRef:cleanText(rawReview.assessmentRef||'',240)}:null;
  return {
    ...value,
    schemaVersion:cleanText(value.schemaVersion||'invalid'), mode:cleanText(value.mode||'demo'), demoLabel:cleanText(value.demoLabel||'DEMO • SYNTHETIC'),
    run:{id:cleanText(run.id||'unavailable'),repository:cleanText(run.repository||'unavailable'),planIdentity:cleanText(run.planIdentity||'unavailable'),planRevision:Number.isSafeInteger(run.planRevision)?run.planRevision:0,policyIdentity:cleanText(run.policyIdentity||'unavailable'),baseCommit:cleanText(run.baseCommit||'unavailable'),headCommit:cleanText(run.headCommit||'unavailable'),treeId:cleanText(run.treeId||'unavailable'),patchSha256:cleanText(run.patchSha256||'unavailable'),authorizationCeiling:CEILINGS.has(run.authorizationCeiling)?run.authorizationCeiling:'plan'},
    source:{snapshotId:cleanText(source.snapshotId||'unavailable'),sequence:Number.isSafeInteger(source.sequence)?source.sequence:0,observedAt:cleanText(source.observedAt||'unavailable'),receivedAt:cleanText(source.receivedAt||'unavailable'),continuity:CONTINUITY.has(source.continuity)?source.continuity:'error',freshness:['current','stale','unknown'].includes(source.freshness)?source.freshness:'unknown',capabilityIdentity:cleanText(source.capabilityIdentity||'unavailable'),realm:cleanText(source.realm||'invalid'),eventErrors:list(source.eventErrors).map(x=>cleanText(x,160)).slice(0,20)},
    requirements:{gateIds:list(requirements.gateIds).map(x=>cleanText(x,80)).slice(0,100),reviewRequired:requirements.reviewRequired===true,policyIdentity:cleanText(requirements.policyIdentity||'unavailable')},
    workflow:{phase:PHASES.has(workflow.phase)?workflow.phase:'LAUNCH',coreState:cleanText(workflow.coreState||'Invalid'),desiredState:cleanText(workflow.desiredState||'UNKNOWN'),observedState:cleanText(workflow.observedState||'UNKNOWN'),cleanup:cleanText(workflow.cleanup||'unknown'),activeOperationCount:Number.isSafeInteger(workflow.activeOperationCount)?workflow.activeOperationCount:null,quiescenceEvidenceRef:workflow.quiescenceEvidenceRef?cleanText(workflow.quiescenceEvidenceRef):null},
    focus:{title:cleanText(focus.title||'State unavailable'),explanation:cleanText(focus.explanation||'Malformed or unsupported presentation snapshot.'),blockerCode:focus.blockerCode?cleanText(focus.blockerCode):null,nextActionId:cleanText(focus.nextActionId||''),nextActionReason:cleanText(focus.nextActionReason||'Reconciliation required.')},
    gates,attempts,review,actions,integrations,measurements,
    publication:object(value.publication)?value.publication:{},plan:object(value.plan)?value.plan:{}
  };
}

export function validateSnapshot(snapshot) {
  const errors=[];
  if(!object(snapshot)) return ['snapshot must be an object'];
  if(snapshot.schemaVersion!=='forge.presentation/0.1') errors.push('unsupported snapshot schema');
  if(snapshot.mode!=='demo') errors.push('prototype rejects non-demo mode');
  if(snapshot.demoLabel!=='DEMO • SYNTHETIC') errors.push('demo label missing');
  if(!object(snapshot.run)||!snapshot.run.id||!snapshot.run.planIdentity||!snapshot.run.headCommit||!snapshot.run.treeId||!snapshot.run.policyIdentity) errors.push('run identity incomplete');
  if(!CEILINGS.has(snapshot.run?.authorizationCeiling)) errors.push('invalid authorization ceiling');
  if(!object(snapshot.source)||snapshot.source.realm!=='fixture') errors.push('prototype accepts fixture realm only');
  if(!Number.isSafeInteger(snapshot.source?.sequence)||snapshot.source.sequence<1) errors.push('invalid sequence');
  if(!CONTINUITY.has(snapshot.source?.continuity)) errors.push('invalid continuity');
  if(!object(snapshot.workflow)||!PHASES.has(snapshot.workflow.phase)) errors.push('invalid workflow phase');
  if(!object(snapshot.focus)) errors.push('focus missing');
  if(!object(snapshot.requirements)||!Array.isArray(snapshot.requirements.gateIds)||snapshot.requirements.gateIds.length===0) errors.push('policy gate requirements missing');
  else {
    if(snapshot.requirements.gateIds.some(id=>typeof id!=='string'||!id)) errors.push('invalid required gate id');
    if(new Set(snapshot.requirements.gateIds).size!==snapshot.requirements.gateIds.length) errors.push('duplicate required gate id');
    if(typeof snapshot.requirements.reviewRequired!=='boolean') errors.push('review requirement missing');
    if(snapshot.requirements.policyIdentity!==snapshot.run?.policyIdentity) errors.push('requirements policy identity mismatch');
  }
  for(const key of ['gates','actions','attempts','integrations','measurements']) if(!Array.isArray(snapshot[key])) errors.push(`${key} must be an array`);
  const gateIds=new Set(),receiptIds=new Set();
  for(const gate of list(snapshot.gates)) {
    if(!object(gate)||!gate.id){errors.push('invalid gate row');continue;}
    const id=cleanText(gate.id,80);
    if(gateIds.has(id)) errors.push(`duplicate gate ${id}`);gateIds.add(id);
    if(!OUTCOMES.has(gate.outcome)) errors.push(`invalid outcome for ${id}`);
    if(!gate.receiptId) errors.push(`missing receipt for ${id}`);
    else if(receiptIds.has(gate.receiptId)) errors.push(`duplicate receipt ${cleanText(gate.receiptId,80)}`);else receiptIds.add(gate.receiptId);
    if(gate.outcome==='NOT_APPLICABLE'&&(!gate.notApplicableAllowed||!gate.notApplicableRationale)) errors.push(`invalid N/A for ${id}`);
  }
  for(const action of list(snapshot.actions)) {
    if(!object(action)){errors.push('invalid action row');continue;}
    if(typeof action.id!=='string'||!action.id) errors.push('invalid action id');
    if(!['intent','read'].includes(action.kind)) errors.push(`invalid action kind for ${cleanText(action.id||'unknown',80)}`);
    if(typeof action.available!=='boolean') errors.push(`invalid action availability for ${cleanText(action.id||'unknown',80)}`);
    if(typeof action.reason!=='string'||!action.reason) errors.push(`invalid action rationale for ${cleanText(action.id||'unknown',80)}`);
  }
  if(snapshot.review&&!REVIEW_OUTCOMES.has(snapshot.review.outcome)) errors.push('invalid review outcome');
  if(list(snapshot.gates).length>100||list(snapshot.actions).length>100) errors.push('array bound exceeded');
  return errors;
}

function aggregateValid(snapshot) {
  const reasons=[];let rank=0;
  const set=(next,reason)=>{rank=Math.max(rank,{PASS:0,INCOMPLETE:1,FAIL:2,ERROR:3}[next]);reasons.push(reason);};
  const byId=new Map(snapshot.gates.map(g=>[g.id,g]));
  for(const id of snapshot.requirements.gateIds){
    const gate=byId.get(id);
    if(!gate){set('INCOMPLETE',`${id}: required policy gate is missing`);continue;}
    const exact=gate.headCommit===snapshot.run.headCommit&&gate.treeId===snapshot.run.treeId&&gate.policyIdentity===snapshot.run.policyIdentity;
    if(gate.currentness!=='current') set('INCOMPLETE',`${gate.label||id}: ${gate.currentness||'unknown'}`);
    else if(!exact) set('ERROR',`${gate.label||id}: invalid source binding`);
    else if(gate.outcome==='ERROR') set('ERROR',`${gate.label||id}: protocol/error evidence`);
    else if(gate.outcome==='FAIL') set('FAIL',`${gate.label||id}: failed`);
    else if(gate.outcome==='INCOMPLETE') set('INCOMPLETE',`${gate.label||id}: incomplete`);
  }
  if(snapshot.source.freshness!=='current') set('INCOMPLETE','source is stale or unknown');
  if(snapshot.source.continuity==='gap'||snapshot.source.continuity==='unknown') set('INCOMPLETE','source continuity is incomplete');
  if(snapshot.source.continuity==='error'||snapshot.source.continuity==='out-of-order') set('ERROR','source continuity is invalid');
  if(snapshot.workflow.cleanup==='unknown') set('INCOMPLETE','cleanup is unknown');
  if(snapshot.requirements.reviewRequired){
    if(!snapshot.review) set('INCOMPLETE','required independent review is missing');
    else {
      const exact=snapshot.review.headCommit===snapshot.run.headCommit&&snapshot.review.treeId===snapshot.run.treeId&&snapshot.review.patchSha256===snapshot.run.patchSha256;
      if(!exact) set('INCOMPLETE','review belongs to another source identity');
      if(snapshot.review.independence!=='accepted') set('INCOMPLETE','review independence not accepted');
      if(snapshot.review.outcome==='INCOMPLETE') set('INCOMPLETE','review is incomplete');
      if(snapshot.review.outcome==='FAIL') set('FAIL','review failed');
      if(snapshot.review.outcome==='ERROR') set('ERROR','review errored');
    }
  }
  return {outcome:['PASS','INCOMPLETE','FAIL','ERROR'][rank],reasons};
}

export function aggregate(snapshot) {
  const errors=validateSnapshot(snapshot);
  return errors.length?{outcome:'ERROR',reasons:errors}:aggregateValid(snapshot);
}

export function project(snapshot) {
  const errors=validateSnapshot(snapshot),safe=safeSnapshot(snapshot);
  if(errors.length&&!safe.focus.blockerCode) safe.focus.blockerCode='INVALID_SNAPSHOT';
  const aggregateResult=errors.length?{outcome:'ERROR',reasons:errors}:aggregateValid(safe);
  const blocked=errors.length>0||safe.source.freshness!=='current'||safe.source.continuity!=='complete'||safe.workflow.cleanup==='unknown';
  const actions=safe.actions.map(action=>consequential(action)&&blocked?{...action,available:false,reason:`Reconciliation required: ${safe.source.freshness}/${safe.source.continuity}; cleanup ${safe.workflow.cleanup}.`}:{...action});
  const usage=typeof snapshot?.usage?.fixtureScenario==='string'?usageForScenario(snapshot.usage.fixtureScenario):usageForScenario('launch');
  return {...safe,usage,validation:{valid:errors.length===0,errors},aggregate:aggregateResult,actions};
}

function merge(base,patch){
  if(Array.isArray(patch)||patch===null||typeof patch!=='object') return structuredClone(patch);
  const out=object(base)?structuredClone(base):{};
  for(const [key,value] of Object.entries(patch)) out[key]=merge(out[key],value);
  return out;
}

export function reduceEvents(events,baseSnapshot) {
  const errors=[],seen=new Map();let expected=1,last=null,current=object(baseSnapshot)?structuredClone(baseSnapshot):{};
  if(!Array.isArray(events)){errors.push('events must be an array');events=[];}
  let runId=baseSnapshot?.run?.id;
  for(const event of events.slice(0,1000)){
    if(!object(event)){errors.push('event must be an object');continue;}
    if(!runId&&typeof event.runId==='string'&&event.runId)runId=event.runId;
    const eventId=typeof event.eventId==='string'&&event.eventId?event.eventId:null;
    const encoded=JSON.stringify(event);
    if(eventId&&seen.has(eventId)){if(seen.get(eventId)!==encoded)errors.push(`conflicting duplicate ${cleanText(eventId,80)}`);continue;}
    if(eventId)seen.set(eventId,encoded);
    if(event.schemaVersion!=='forge.event/0.1')errors.push('unsupported event schema');
    if(!eventId)errors.push('invalid event id');
    if(event.runId!==runId)errors.push('event run identity mismatch');
    if(!Number.isSafeInteger(event.sequence)||event.sequence<1){errors.push('invalid event sequence');continue;}
    if(event.kind!=='snapshot'||!object(event.data))errors.push('invalid snapshot event payload');
    if(event.data?.run?.id&&event.data.run.id!==runId)errors.push('snapshot run identity mismatch');
    if(event.data?.source?.sequence!==undefined&&event.data.source.sequence!==event.sequence)errors.push('snapshot sequence mismatch');
    if(event.sequence<expected){errors.push(`out-of-order sequence ${event.sequence}`);continue;}
    if(event.sequence>expected)errors.push(`sequence gap ${expected}-${event.sequence-1}`);
    expected=event.sequence+1;
    if(event.kind==='snapshot'&&object(event.data)){
      current=merge(current,event.data);current.source=object(current.source)?current.source:{};
      current.source.sequence=event.sequence;current.source.observedAt=cleanText(event.observedAt||current.source.observedAt||'unavailable');last=event;
    }
  }
  const continuity=errors.some(x=>x.includes('gap'))?'gap':errors.some(x=>x.includes('out-of-order'))?'out-of-order':errors.length?'error':'complete';
  current=safeSnapshot(current);current.source.continuity=continuity;current.source.eventErrors=errors.map(x=>cleanText(x,160)).slice(0,20);
  return {last,errors,continuity,baseSnapshot,snapshot:current};
}
