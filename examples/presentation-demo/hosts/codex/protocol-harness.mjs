#!/usr/bin/env node
import { spawn } from 'node:child_process';
import readline from 'node:readline';

const child = spawn(process.execPath,[new URL('./mcp-server.mjs',import.meta.url).pathname],{stdio:['pipe','pipe','inherit']});
const rl = readline.createInterface({input:child.stdout});
const pending = new Map();
rl.on('line',(line)=>{ const msg=JSON.parse(line); pending.get(msg.id)?.(msg); pending.delete(msg.id); });
let id=0;
const call=(method,params={})=>new Promise((resolve,reject)=>{ const next=++id; pending.set(next,resolve); child.stdin.write(`${JSON.stringify({jsonrpc:'2.0',id:next,method,params})}\n`,(e)=>e&&reject(e)); });
const init=await call('initialize',{protocolVersion:'2025-06-18',capabilities:{},clientInfo:{name:'bounded-harness',version:'0.1'}});
child.stdin.write(`${JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized',params:{}})}\n`);
const listed=await call('tools/list');
const rendered=await call('tools/call',{name:'forge_demo_status',arguments:{scenario:process.argv[2]||'failed-gate',width:Number(process.argv[3])||80,presentation:'harness'}});
process.stdout.write(`HARNESS RENDER — MCP protocol, not installed Codex\ninitialize=${init.result?.serverInfo?.name}\ntools=${listed.result.tools.map(x=>x.name).join(',')}\n\n${rendered.result.content[0].text}\n`);
child.stdin.end();
await new Promise((resolve)=>child.on('exit',resolve));
