import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { handleMessage, resetProtocolForTest } from '../hosts/codex/mcp-server.mjs';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
test.beforeEach(()=>resetProtocolForTest());
test('MCP requires initialize and rejects repeats',()=>{
  assert.equal(handleMessage({jsonrpc:'2.0',id:1,method:'tools/list'}).error.message,'Not initialized');
  assert.equal(handleMessage({jsonrpc:'2.0',id:2,method:'initialize',params:{}}).result.serverInfo.name,'forge-demo-text');
  assert.equal(handleMessage({jsonrpc:'2.0',id:3,method:'initialize',params:{}}).error.message,'Already initialized');
});
test('MCP lists two bounded tools and renders shared fixture',()=>{
  handleMessage({jsonrpc:'2.0',id:1,method:'initialize',params:{}});
  const list=handleMessage({jsonrpc:'2.0',id:2,method:'tools/list'});assert.equal(list.result.tools.length,2);
  const call=handleMessage({jsonrpc:'2.0',id:3,method:'tools/call',params:{name:'forge_demo_status',arguments:{scenario:'failed-gate',width:80}}});
  assert.match(call.result.content[0].text,/Mutation evidence is incomplete/);assert.match(call.result.content[0].text,/NATIVE DEMO SURFACE/);assert.equal(call.result.isError,false);
  const harness=handleMessage({jsonrpc:'2.0',id:4,method:'tools/call',params:{name:'forge_demo_status',arguments:{scenario:'failed-gate',width:80,presentation:'harness'}}});assert.match(harness.result.content[0].text,/HARNESS RENDER/);
});
test('MCP returns protocol errors for malformed, unknown and bad arguments',()=>{
  assert.equal(handleMessage(null).error.code,-32600);handleMessage({jsonrpc:'2.0',id:1,method:'initialize',params:{}});
  assert.equal(handleMessage({jsonrpc:'2.0',id:2,method:'wat'}).error.code,-32601);
  assert.equal(handleMessage({jsonrpc:'2.0',id:3,method:'tools/call',params:{name:'forge_demo_status',arguments:{scenario:'nope'}}}).error.code,-32602);
});
test('stdio endpoint handles parse errors, EOF and exits cleanly',()=>{
  const proc=spawnSync(process.execPath,[path.join(root,'hosts/codex/mcp-server.mjs')],{input:'not-json\n',encoding:'utf8',timeout:3000});
  assert.equal(proc.status,0);assert.equal(JSON.parse(proc.stdout).error.code,-32700);
});
