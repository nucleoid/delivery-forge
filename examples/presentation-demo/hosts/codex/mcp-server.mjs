#!/usr/bin/env node
import readline from 'node:readline';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { getScenario } from '../../src/fixtures.mjs';
import { renderCompact,renderExpanded,renderUsageDetail } from '../../src/render-text.mjs';
import { makeIntent, previewIntent } from '../../src/intents.mjs';

const MAX_LINE = 65536;
const VERSION = '2025-06-18';
let initialized = false;

const ok = (id, result) => ({jsonrpc:'2.0',id,result});
const err = (id, code, message, data) => ({jsonrpc:'2.0',id:id ?? null,error:{code,message,...(data?{data}:{})}});
const text = (value) => ({content:[{type:'text',text:value}],isError:false});

export function handleMessage(message) {
  if (!message || message.jsonrpc !== '2.0' || typeof message.method !== 'string') return err(message?.id,-32600,'Invalid Request');
  if (message.method === 'initialize') {
    if (initialized) return err(message.id,-32600,'Already initialized');
    initialized = true;
    return ok(message.id,{protocolVersion:VERSION,capabilities:{tools:{}},serverInfo:{name:'forge-demo-text',version:'0.1.0'},instructions:'DEMO ONLY: synthetic fixture renderer; zero live adapters or authority.'});
  }
  if (message.method === 'notifications/initialized' || message.method === 'initialized') return null;
  if (!initialized) return err(message.id,-32002,'Not initialized');
  if (message.method === 'tools/list') return ok(message.id,{tools:[
    {name:'forge_demo_status',description:'Render a DEMO • SYNTHETIC Forge fixture as native text.',inputSchema:{type:'object',additionalProperties:false,properties:{scenario:{type:'string'},width:{type:'integer',minimum:40,maximum:120},presentation:{type:'string',enum:['native','harness']},view:{type:'string',enum:['expanded','compact','usage']}},required:['scenario']}},
    {name:'forge_demo_preview_intent',description:'Preview a bound fixture intent. Always sideEffects=0 and authorityIssued=false.',inputSchema:{type:'object',additionalProperties:false,properties:{scenario:{type:'string'},action:{type:'string',enum:['request.pause','request.stop','request.resume','request.repair','request.publish-pr']}},required:['scenario','action']}}
  ]});
  if (message.method === 'tools/call') {
    const name = message.params?.name, args = message.params?.arguments || {};
    try {
      const {snapshot} = getScenario(String(args.scenario || ''));
      if (name === 'forge_demo_status') {const options={width:Number(args.width)||80,presentation:args.presentation==='harness'?'harness':'native'};const output=args.view==='compact'?renderCompact(snapshot,options).join('\n'):args.view==='usage'?renderUsageDetail(snapshot,options):renderExpanded(snapshot,options);return ok(message.id,text(output));}
      if (name === 'forge_demo_preview_intent') return ok(message.id,text(JSON.stringify(previewIntent(snapshot,makeIntent(snapshot,String(args.action||''))),null,2)));
      return err(message.id,-32602,'Unknown tool');
    } catch (error) { return err(message.id,-32602,'Invalid tool arguments',{detail:String(error.message).slice(0,300)}); }
  }
  if (message.method === 'ping') return ok(message.id,{});
  return err(message.id,-32601,'Method not found');
}

export function resetProtocolForTest() { initialized = false; }

export function isDirectExecution(moduleUrl, argvPath, platform=process.platform) {
  if(!argvPath) return false;
  const paths=platform==='win32'?path.win32:path;
  const normalize=(value)=>{
    let decoded=value instanceof URL||String(value).startsWith('file:')?fileURLToPath(value):String(value);
    if(platform==='win32'&&/^\/[A-Za-z]:\//.test(decoded)) decoded=decoded.slice(1);
    const resolved=paths.resolve(decoded);
    return platform==='win32'?resolved.toLowerCase():resolved;
  };
  return normalize(moduleUrl)===normalize(argvPath);
}

if (isDirectExecution(import.meta.url,process.argv[1])) {
  const rl = readline.createInterface({input:process.stdin,crlfDelay:Infinity,terminal:false});
  rl.on('line',(line)=>{
    let response;
    if (Buffer.byteLength(line,'utf8') > MAX_LINE) response = err(null,-32600,'Request exceeds 64KiB');
    else try { response = handleMessage(JSON.parse(line)); } catch { response = err(null,-32700,'Parse error'); }
    if (response) process.stdout.write(`${JSON.stringify(response)}\n`);
  });
  rl.on('error',(error)=>process.stdout.write(`${JSON.stringify(err(null,-32603,'Input error',{detail:String(error.message).slice(0,200)}))}\n`));
}
