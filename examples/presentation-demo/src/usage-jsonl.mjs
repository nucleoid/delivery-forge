import { closeSync, existsSync, fsyncSync, openSync, readFileSync, statSync, writeSync } from 'node:fs';
import { createUsageLedger, replayUsage, summarizeUsage, SYNTHETIC_USAGE_EVENTS, USAGE_SCHEMA } from './usage.mjs';

export const JSONL_SCHEMA='forge.usage.synthetic-jsonl/0.1';
const MAX_BYTES=262144,MAX_RECORDS=256,MAX_LINE_BYTES=32768;
const own=(value,key)=>Object.prototype.hasOwnProperty.call(value,key);
const object=value=>value&&typeof value==='object'&&!Array.isArray(value);
const allowed=(value,keys)=>object(value)&&Object.keys(value).every(key=>keys.includes(key));
const header=()=>({schemaVersion:JSONL_SCHEMA,type:'owned-synthetic-ledger',usageSchemaVersion:USAGE_SCHEMA,synthetic:true});

function durableAppend(path,text,flags='a'){
 const fd=openSync(path,flags,0o600);
 try{writeSync(fd,text);fsyncSync(fd);}finally{closeSync(fd);}
}

export function initializeOwnedUsageJsonl(path){
 if(typeof path!=='string'||!path)throw new Error('explicit ledger path required');
 durableAppend(path,`${JSON.stringify(header())}\n`,'wx');
}

export function appendOwnedUsageFixture(path,eventName){
 if(typeof path!=='string'||!path)throw new Error('explicit ledger path required');
 if(!own(SYNTHETIC_USAGE_EVENTS,eventName))throw new Error('only bundled synthetic fixture events are admitted');
 if(!existsSync(path))throw new Error('owned synthetic ledger is not initialized');
 const size=statSync(path).size;if(size>MAX_BYTES)throw new Error('owned synthetic ledger exceeds 256KiB');
 const content=readFileSync(path,{encoding:'utf8'}),existingLines=content.split('\n');if(!content.endsWith('\n'))throw new Error('owned synthetic ledger has a truncated tail');if(existingLines.length-2>=MAX_RECORDS)throw new Error('owned synthetic ledger exceeds record bound');
 const first=existingLines[0];
 let marker;try{marker=JSON.parse(first);}catch{throw new Error('owned synthetic ledger header invalid');}
 if(!allowed(marker,['schemaVersion','type','usageSchemaVersion','synthetic'])||marker.schemaVersion!==JSONL_SCHEMA||marker.type!=='owned-synthetic-ledger'||marker.usageSchemaVersion!==USAGE_SCHEMA||marker.synthetic!==true)throw new Error('owned synthetic ledger header invalid');
 const record={schemaVersion:JSONL_SCHEMA,type:'fixture-observation',eventName,event:structuredClone(SYNTHETIC_USAGE_EVENTS[eventName])};
 const line=`${JSON.stringify(record)}\n`;
 if(Buffer.byteLength(line)>MAX_LINE_BYTES||size+Buffer.byteLength(line)>MAX_BYTES)throw new Error('owned synthetic ledger size bound exceeded');
 durableAppend(path,line);
}

export function replayOwnedUsageJsonl(path){
 if(typeof path!=='string'||!path)throw new Error('explicit ledger path required');
 const bytes=readFileSync(path);
 if(bytes.length>MAX_BYTES)throw new Error('owned synthetic ledger exceeds 256KiB');
 const text=bytes.toString('utf8'),terminated=text.endsWith('\n'),lines=text.split('\n');
 if(terminated)lines.pop();
 const conditions=[];
 if(!terminated){conditions.push('truncated JSONL tail excluded; evidence continuity uncertain');lines.pop();}
 if(lines.length>MAX_RECORDS+1)throw new Error('owned synthetic ledger exceeds record bound');
 let marker=null;try{marker=JSON.parse(lines.shift()||'');}catch{conditions.push('malformed owned-ledger header; evidence not admitted');}
 if(!allowed(marker,['schemaVersion','type','usageSchemaVersion','synthetic'])||marker?.schemaVersion!==JSONL_SCHEMA||marker?.type!=='owned-synthetic-ledger'||marker?.usageSchemaVersion!==USAGE_SCHEMA||marker?.synthetic!==true)conditions.push('owned synthetic ledger identity invalid; evidence not admitted');
 const admitted=conditions.some(value=>value.includes('not admitted'))?[]:lines.flatMap((line,index)=>{
  if(Buffer.byteLength(line)>MAX_LINE_BYTES){conditions.push(`JSONL record ${index+1} exceeds line bound; excluded`);return[];}
  let record;try{record=JSON.parse(line);}catch{conditions.push(`malformed JSONL record ${index+1}; excluded`);return[];}
  if(!allowed(record,['schemaVersion','type','eventName','event'])||record.schemaVersion!==JSONL_SCHEMA||record.type!=='fixture-observation'||!own(SYNTHETIC_USAGE_EVENTS,record.eventName)||!object(record.event)||JSON.stringify(record.event)!==JSON.stringify(SYNTHETIC_USAGE_EVENTS[record.eventName])){conditions.push(`invalid JSONL metadata at record ${index+1}; excluded`);return[];}
  return[record.event];
 });
 const state=replayUsage(admitted,createUsageLedger());
 state.conditions.push(...conditions);
 return{state,summary:summarizeUsage(state),conditions,recordCount:admitted.length,bytes:bytes.length,mutated:false};
}
