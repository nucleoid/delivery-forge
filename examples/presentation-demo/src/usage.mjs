import { cleanText } from './safety.mjs';

export const USAGE_SCHEMA='forge.usage/0.1';
const MAX_EVENTS=256,MAX_BYTES=262144,obj=v=>v&&typeof v==='object'&&!Array.isArray(v),clone=structuredClone;
export const SYNTHETIC_RATES=Object.freeze({
 writer_v1:Object.freeze({id:'synthetic.writer.v1',synthetic:true,currency:'USD',unit:'nano-USD-per-token (integer; equivalent to USD-per-million / 1000)',provider:'demo.openai',backend:'demo-api',sku:'demo.openai.writer',tier:'synthetic-standard',rates:Object.freeze({ordinary:2000,read:200,write5m:2500,write1h:2500,output:10000}),asOf:'2026-10-19T10:00:00.000Z',effectiveFrom:'synthetic-fixture-v1',source:'fixture://rates/writer-v1'}),
 reviewer_v1:Object.freeze({id:'synthetic.reviewer.v1',synthetic:true,currency:'USD',unit:'nano-USD-per-token (integer; equivalent to USD-per-million / 1000)',provider:'demo.anthropic',backend:'demo-api',sku:'demo.anthropic.reviewer',tier:'synthetic-standard',rates:Object.freeze({ordinary:3000,read:300,write5m:3750,write1h:6000,output:15000}),asOf:'2026-10-19T10:00:00.000Z',effectiveFrom:'synthetic-fixture-v1',source:'fixture://rates/reviewer-v1'})
});
const bad=(reason,raw=null)=>({valid:false,reason,raw:obj(raw)?clone(raw):null});
const integer=(v,name,optional=false)=>v==null&&optional?{value:null}:!Number.isSafeInteger(v)||v<0?{error:`${name} must be a nonnegative safe integer`}:{value:v};

export function normalizeUsage(raw){
 if(!obj(raw))return bad('observation must be an object');
 const values={};for(const key of ['input','cacheRead','cacheWrite','output']){const c=integer(raw[key],key);if(c.error)return bad(c.error,raw);values[key]=c.value;}
 const one=integer(raw.cacheWrite1h,'cacheWrite1h',true),reason=integer(raw.reasoning,'reasoning',true);if(one.error||reason.error)return bad(one.error||reason.error,raw);
 if(!['inclusive','exclusive'].includes(raw.inputSemantics))return bad('inputSemantics must be inclusive or exclusive',raw);
 if(one.value!==null&&one.value>values.cacheWrite)return bad('cacheWrite1h exceeds cacheWrite',raw);
 if(reason.value!==null&&reason.value>values.output)return bad('reasoning exceeds output',raw);
 const ordinary=raw.inputSemantics==='inclusive'?values.input-values.cacheRead-values.cacheWrite:values.input;if(ordinary<0)return bad('inclusive input is smaller than cache subsets',raw);
 const inputTotal=ordinary+values.cacheRead+values.cacheWrite;if(!Number.isSafeInteger(inputTotal))return bad('normalized input exceeds safe integer',raw);
 return {valid:true,inputOrdinary:ordinary,inputCacheRead:values.cacheRead,inputCacheWrite:values.cacheWrite,inputCacheWrite1h:one.value,inputTotal,outputTotal:values.output,outputReasoningSubset:reason.value,presence:{cacheRead:raw.cacheReadPresent===true,cacheWrite:raw.cacheWritePresent===true,cacheWrite1h:raw.cacheWrite1h!==undefined,reasoning:raw.reasoning!==undefined},mapping:cleanText(raw.mapping||'unknown',120),inputSemantics:raw.inputSemantics};
}
export function priceUsage(n,rate){
 if(!n?.valid)return{priced:false,reason:n?.reason||'invalid usage'};
 if(!obj(rate)||rate.synthetic!==true||rate.currency!=='USD')return{priced:false,reason:'rate snapshot unavailable'};
 if(!n.presence.cacheRead||!n.presence.cacheWrite)return{priced:false,reason:'cache field presence unknown'};
 const r=rate.rates||{};for(const key of ['ordinary','read','write5m','write1h','output'])if(!Number.isSafeInteger(r[key])||r[key]<0)return{priced:false,reason:`rate ${key} unavailable`};
 let one=0,five=n.inputCacheWrite;if(r.write5m!==r.write1h){if(n.inputCacheWrite1h===null)return{priced:false,reason:'cache-write retention split unknown'};one=n.inputCacheWrite1h;five-=one;}else if(n.inputCacheWrite1h!==null){one=n.inputCacheWrite1h;five-=one;}
 const nanoUsd=n.inputOrdinary*r.ordinary+n.inputCacheRead*r.read+five*r.write5m+one*r.write1h+n.outputTotal*r.output;
 return Number.isSafeInteger(nanoUsd)?{priced:true,nanoUsd,rateSnapshotId:rate.id,components:{write5m:five,write1h:one},actualChargeUsd:null}:{priced:false,reason:'cost exceeds exact arithmetic bound'};
}
function parse(raw){
 if(!obj(raw))return bad('usage observation must be an object');
 for(const key of ['observationId','incurrenceId','attemptId','actorId','actorRole','phase','requestedModel','provider','backend','tier','sourceEpoch'])if(typeof raw[key]!=='string'||!raw[key]||raw[key].length>160)return bad(`${key} invalid`,raw);
 if(!Number.isSafeInteger(raw.sequence)||raw.sequence<1||!Number.isSafeInteger(raw.revision)||raw.revision<1)return bad('sequence or revision invalid',raw);
 if(!['host-estimated','provider-partial','provider-final','unknown'].includes(raw.stage))return bad('stage invalid',raw);
 const normalized=normalizeUsage(raw.usage);if(!normalized.valid)return bad(normalized.reason,raw);
 const effective=typeof raw.effectiveModel==='string'&&raw.effectiveModel?cleanText(raw.effectiveModel,160):null,rate=raw.rateKey?SYNTHETIC_RATES[raw.rateKey]:null;
 const binding=effective&&rate&&rate.sku===effective&&rate.provider===raw.provider&&rate.backend===raw.backend&&rate.tier===raw.tier;
 const priced=binding?priceUsage(normalized,rate):{priced:false,reason:raw.unpricedReason||'provider/backend/tier/model rate binding unresolved'};
 return{valid:true,...Object.fromEntries(['observationId','incurrenceId','attemptId','actorId','actorRole','phase','requestedModel','provider','backend','tier','sourceEpoch'].map(k=>[k,cleanText(raw[k],160)])),effectiveModel:effective,sequence:raw.sequence,revision:raw.revision,stage:raw.stage,final:raw.stage==='provider-final',normalized,priced,rateSnapshot:binding?clone(rate):null,unpricedReason:priced.priced?null:cleanText(priced.reason,240),coverage:['mirror','inclusive-parent'].includes(raw.coverage)?raw.coverage:'primary',observedAt:cleanText(raw.observedAt||'synthetic',120),synthetic:true};
}
export const createUsageLedger=()=>({schemaVersion:USAGE_SCHEMA,sourceEpoch:null,lastSequence:0,seen:{},contributions:{},conditions:[],freshness:'current',revoked:false,actualChargeUsd:null});
function validNormalized(n){return obj(n)&&n.valid===true&&['inputOrdinary','inputCacheRead','inputCacheWrite','inputTotal','outputTotal'].every(k=>Number.isSafeInteger(n[k])&&n[k]>=0)&&(n.inputCacheWrite1h===null||Number.isSafeInteger(n.inputCacheWrite1h))&&(n.outputReasoningSubset===null||Number.isSafeInteger(n.outputReasoningSubset))&&obj(n.presence)&&['cacheRead','cacheWrite','cacheWrite1h','reasoning'].every(k=>typeof n.presence[k]==='boolean')&&typeof n.mapping==='string'&&['inclusive','exclusive'].includes(n.inputSemantics);}
function validContribution(row){
 if(!obj(row)||row.valid!==true||!['observationId','incurrenceId','attemptId','actorId','actorRole','phase','requestedModel','provider','backend','tier','sourceEpoch','stage'].every(k=>typeof row[k]==='string'&&row[k].length>0&&row[k].length<=160)||!Number.isSafeInteger(row.sequence)||row.sequence<1||!Number.isSafeInteger(row.revision)||row.revision<1||!validNormalized(row.normalized)||!obj(row.priced)||typeof row.priced.priced!=='boolean')return false;
 if(row.priced.priced){const rate=Object.values(SYNTHETIC_RATES).find(value=>value.id===row.priced.rateSnapshotId),recomputed=rate&&priceUsage(row.normalized,rate);if(!rate||JSON.stringify(row.rateSnapshot)!==JSON.stringify(rate)||rate.provider!==row.provider||rate.backend!==row.backend||rate.tier!==row.tier||rate.sku!==row.effectiveModel||!recomputed.priced||recomputed.nanoUsd!==row.priced.nanoUsd)return false;}
 if(!row.priced.priced&&(typeof row.priced.reason!=='string'||row.priced.reason.length>240||row.rateSnapshot!==null))return false;
 return ['provider-partial','provider-final','host-estimated','unknown'].includes(row.stage)&&['primary','mirror','inclusive-parent'].includes(row.coverage)&&typeof row.observedAt==='string'&&row.observedAt.length<=120&&row.synthetic===true;
}
function validateCheckpoint(value){
 if(!obj(value)||value.schemaVersion!==USAGE_SCHEMA||!obj(value.seen)||!obj(value.contributions)||!Array.isArray(value.conditions))throw new Error('invalid usage checkpoint');
 if(Object.keys(value.seen).length>MAX_EVENTS||Object.keys(value.contributions).length>MAX_EVENTS||value.conditions.length>MAX_EVENTS)throw new Error('usage checkpoint exceeds record bound');
 if(!Object.values(value.seen).every(v=>typeof v==='string'&&Buffer.byteLength(v)<=MAX_BYTES)||!Object.values(value.contributions).every(validContribution)||!value.conditions.every(v=>typeof v==='string'&&v.length<=240))throw new Error('invalid usage checkpoint metadata');
 if(value.sourceEpoch!==null&&(typeof value.sourceEpoch!=='string'||value.sourceEpoch.length>160)||!Number.isSafeInteger(value.lastSequence)||value.lastSequence<0||!['current','stale','revoked'].includes(value.freshness)||typeof value.revoked!=='boolean'||value.actualChargeUsd!==null)throw new Error('invalid usage checkpoint state');
 return value;
}
export function replayUsage(events,checkpoint=createUsageLedger()){
 const state=clone(validateCheckpoint(checkpoint));if(!Array.isArray(events))events=[];
 for(const raw of events.slice(0,MAX_EVENTS)){
  if(!obj(raw)){state.conditions.push('malformed observation quarantined');continue;}
  if(raw.type==='source-state'){if(raw.state==='revoked'){state.revoked=true;state.freshness='revoked';state.conditions.push('source revoked; new observations rejected');}else if(raw.state==='stale')state.freshness='stale';else if(raw.state==='reconciled'&&!state.revoked)state.freshness='current';continue;}
  if(raw.type==='cumulative'){state.conditions.push(raw.baseline&&typeof raw.cohortId==='string'&&Number.isSafeInteger(raw.totalTokens)&&raw.totalTokens>=0?`cumulative baseline ${cleanText(raw.cohortId,80)} retained; not charged`:'cumulative checkpoint requires reconciliation');continue;}
  if(state.revoked){state.conditions.push('observation rejected after source revocation');continue;}
  const row=parse(raw);if(!row.valid){state.conditions.push(`quarantined: ${cleanText(row.reason,160)}`);continue;}
  if(state.sourceEpoch&&row.sourceEpoch!==state.sourceEpoch){state.conditions.push('source epoch changed; reconciliation required');continue;}state.sourceEpoch||=row.sourceEpoch;
  if(state.lastSequence&&row.sequence>state.lastSequence+1)state.conditions.push(`sequence gap ${state.lastSequence+1}-${row.sequence-1}`);state.lastSequence=Math.max(state.lastSequence,row.sequence);
  const fingerprint=JSON.stringify(raw);if(state.seen[row.observationId]){if(state.seen[row.observationId]!==fingerprint)state.conditions.push(`conflicting duplicate ${row.observationId}`);continue;}state.seen[row.observationId]=fingerprint;
  if(row.coverage!=='primary'){state.conditions.push(`${row.coverage} ${row.observationId} retained as corroboration`);continue;}
  const old=state.contributions[row.incurrenceId];if(old&&row.revision<old.revision){state.conditions.push(`out-of-order revision ${row.observationId} ignored`);continue;}if(old&&row.revision===old.revision){if(JSON.stringify(row.normalized)!==JSON.stringify(old.normalized))state.conditions.push(`conflicting revision ${row.incurrenceId}`);continue;}state.contributions[row.incurrenceId]=row;
 }
 return state;
}
export function serializeUsageCheckpoint(state){const text=JSON.stringify(state);if(Buffer.byteLength(text)>MAX_BYTES)throw new Error('usage checkpoint exceeds 256KiB');return text;}
export function deserializeUsageCheckpoint(text){if(typeof text!=='string'||Buffer.byteLength(text)>MAX_BYTES)throw new Error('invalid usage checkpoint size');return validateCheckpoint(JSON.parse(text));}
const usd=n=>(n/1e9).toFixed(6);
export function summarizeUsage(state){
 const rawRows=obj(state?.contributions)?Object.values(state.contributions):[],rows=rawRows.filter(validContribution),models=new Map();let tokens=0,cost=0,priced=0,unpriced=0,final=0,provisional=0;
 for(const row of rows){const n=row.normalized; tokens+=n.inputTotal+n.outputTotal;row.final?final++:provisional++;row.priced.priced?(cost+=row.priced.nanoUsd,priced++):unpriced++;const key=row.effectiveModel||'unresolved',m=models.get(key)||{model:key,provider:row.provider,requestedModels:[],actors:[],inputTotal:0,inputOrdinary:0,cacheRead:0,cacheWrite:0,cacheWrite1h:0,outputTotal:0,reasoningKnown:0,reasoningUnknown:false,knownCostNanoUsd:0,pricedRequests:0,unpricedRequests:0,rateSnapshots:[]};m.requestedModels.push(row.requestedModel);m.actors.push(`${row.actorRole}:${row.actorId}`);m.inputTotal+=n.inputTotal;m.inputOrdinary+=n.inputOrdinary;m.cacheRead+=n.inputCacheRead;m.cacheWrite+=n.inputCacheWrite;m.cacheWrite1h+=n.inputCacheWrite1h||0;m.outputTotal+=n.outputTotal;n.outputReasoningSubset===null?m.reasoningUnknown=true:m.reasoningKnown+=n.outputReasoningSubset;if(row.priced.priced){m.pricedRequests++;m.knownCostNanoUsd+=row.priced.nanoUsd;m.rateSnapshots.push(row.rateSnapshot);}else m.unpricedRequests++;models.set(key,m);}
 const conditions=[...new Set([...(Array.isArray(state?.conditions)?state.conditions:[]),...(rawRows.length===rows.length?[]:['malformed checkpoint contribution excluded'])])];return{schemaVersion:USAGE_SCHEMA,available:rows.length>0,synthetic:true,label:'USD API-EQUIVALENT ESTIMATE',legend:'SYNTHETIC TOKENS + SYNTHETIC RATES; not an invoice, subscription bill, or actual charge',status:!rows.length?'UNAVAILABLE':unpriced||conditions.some(x=>/gap|conflict|reconciliation|revok|quarantined|malformed|truncated/.test(x))?'PARTIAL':'COMPLETE',knownCostNanoUsd:priced?cost:null,knownCostUsd:priced?usd(cost):null,observedTokens:tokens,observedRequests:rows.length,pricedRequests:priced,unpricedRequests:unpriced,finalRequests:final,provisionalRequests:provisional,actualChargeUsd:null,freshness:state?.freshness||'unknown',conditions,models:[...models.values()].map(m=>({...m,requestedModels:[...new Set(m.requestedModels)],actors:[...new Set(m.actors)],rateSnapshots:[...new Map(m.rateSnapshots.map(r=>[r.id,r])).values()],knownCostUsd:m.pricedRequests?usd(m.knownCostNanoUsd):null})),requests:rows.map(r=>({incurrenceId:r.incurrenceId,attemptId:r.attemptId,phase:r.phase,actorId:r.actorId,actorRole:r.actorRole,requestedModel:r.requestedModel,effectiveModel:r.effectiveModel,stage:r.stage,revision:r.revision,observedTokens:r.normalized.inputTotal+r.normalized.outputTotal,cachePresence:{...r.normalized.presence},mapping:r.normalized.mapping,knownCostUsd:r.priced.priced?usd(r.priced.nanoUsd):null,unpricedReason:r.unpricedReason,rateSnapshotId:r.priced.rateSnapshotId||null}))};
}
const base={sourceEpoch:'synthetic-epoch-1',actorRole:'worker',phase:'EXECUTE',provider:'demo.openai',backend:'demo-api',tier:'synthetic-standard',requestedModel:'demo-writer-alias',effectiveModel:'demo.openai.writer',rateKey:'writer_v1',coverage:'primary',observedAt:'2026-10-19T10:02:00.000Z'};
export const SYNTHETIC_USAGE_EVENTS=Object.freeze({
 writerPartial:Object.freeze({...base,observationId:'obs-writer-a-r1',incurrenceId:'request-writer-a',attemptId:'attemptA',actorId:'demo.writer',sequence:1,revision:1,stage:'provider-partial',usage:{inputSemantics:'inclusive',input:10000,cacheRead:6000,cacheWrite:1000,output:1200,reasoning:300,cacheReadPresent:true,cacheWritePresent:true,mapping:'demo.openai-inclusive/v1'}}),
 writerFinal:Object.freeze({...base,observationId:'obs-writer-a-r2',incurrenceId:'request-writer-a',attemptId:'attemptA',actorId:'demo.writer',sequence:2,revision:2,stage:'provider-final',usage:{inputSemantics:'inclusive',input:10000,cacheRead:6000,cacheWrite:1000,output:2000,reasoning:500,cacheReadPresent:true,cacheWritePresent:true,mapping:'demo.openai-inclusive/v1'}}),
 reviewerFinal:Object.freeze({...base,observationId:'obs-reviewer-b-r1',incurrenceId:'request-reviewer-b',attemptId:'attemptB',actorId:'demo.reviewer',actorRole:'reviewer',phase:'REVIEW',provider:'demo.anthropic',requestedModel:'demo-reviewer-alias',effectiveModel:'demo.anthropic.reviewer',rateKey:'reviewer_v1',sequence:3,revision:1,stage:'provider-final',usage:{inputSemantics:'exclusive',input:1000,cacheRead:4000,cacheWrite:500,cacheWrite1h:200,output:400,reasoning:100,cacheReadPresent:true,cacheWritePresent:true,mapping:'demo.anthropic-exclusive/v1'}}),
 writerRetry:Object.freeze({...base,observationId:'obs-writer-c-r1',incurrenceId:'request-writer-c',attemptId:'retryC',actorId:'demo.writer',sequence:4,revision:1,stage:'provider-final',usage:{inputSemantics:'exclusive',input:500,cacheRead:0,cacheWrite:0,output:100,cacheReadPresent:true,cacheWritePresent:true,mapping:'demo.pi-exclusive/v1'}}),
 unresolved:Object.freeze({...base,observationId:'obs-coordinator-d-r1',incurrenceId:'request-coordinator-d',attemptId:'attemptD',actorId:'demo.coordinator',actorRole:'coordinator',phase:'PLAN',provider:'demo.unresolved',requestedModel:'demo.unresolved.coordinator',effectiveModel:null,rateKey:null,unpricedReason:'effective model and pricing SKU unresolved',sequence:5,revision:1,stage:'provider-final',usage:{inputSemantics:'exclusive',input:700,cacheRead:0,cacheWrite:0,output:100,cacheReadPresent:true,cacheWritePresent:true,mapping:'demo.unknown-exclusive/v1'}})
});
export function usageForScenario(name){if(['launch','plan'].includes(name))return summarizeUsage(createUsageLedger());const full=[SYNTHETIC_USAGE_EVENTS.writerPartial,SYNTHETIC_USAGE_EVENTS.writerFinal,SYNTHETIC_USAGE_EVENTS.reviewerFinal,SYNTHETIC_USAGE_EVENTS.writerRetry,SYNTHETIC_USAGE_EVENTS.unresolved,SYNTHETIC_USAGE_EVENTS.writerFinal,{...SYNTHETIC_USAGE_EVENTS.writerFinal,observationId:'obs-writer-mirror',coverage:'mirror'}],events=name==='execute'?[SYNTHETIC_USAGE_EVENTS.writerPartial]:full,extra=name==='stale-reconnected'?[{type:'source-state',state:'stale'}]:name==='protocol-error'?[{type:'source-state',state:'revoked'}]:[];return summarizeUsage(replayUsage([...events,...extra]));}
