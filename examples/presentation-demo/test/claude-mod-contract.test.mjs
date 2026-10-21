import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const mod=path.join(root,'hosts/claude/mod');
const source=fs.readFileSync(path.join(mod,'hooks/register.ts'),'utf8');

test('Claude mod package is session-local, typed, and owns one pane and command',()=>{
  const manifest=JSON.parse(fs.readFileSync(path.join(mod,'.claude-plugin/plugin.json'),'utf8'));
  const hooks=JSON.parse(fs.readFileSync(path.join(mod,'hooks/hooks.json'),'utf8'));
  assert.equal(manifest.name,'forge-cockpit-demo');assert.equal(manifest.types,'./types/index.d.ts');assert.deepEqual(hooks.modules,['./register.ts']);
  assert.match(source,/\$\.command\.register/);assert.match(source,/\$\.ui\.open/);assert.match(source,/\$\.ui\.resolve/);assert.match(source,/e\.requestId !== PANE/);
});

test('Claude mod call surface excludes authority, model, prompt, process, network, and filesystem APIs',()=>{
  for(const forbidden of ['$.model.','$.tool.','$.agent.','$.process.','$.fs.','$.http.','$.prompt.','$.permission.','child_process','fetch('])assert.doesNotMatch(source,new RegExp(forbidden.replace(/[.$()]/g,'\\$&')));
  const calls=[...source.matchAll(/\$\.([a-z]+)\.([a-zA-Z]+)\s*\(/g)].map((m)=>`${m[1]}.${m[2]}`);
  assert.deepEqual([...new Set(calls)].sort(),['command.register','store.get','store.set','ui.invalidate','ui.open','ui.resolve']);
});

test('Claude mod exposes four native views and fail-closed no-argument terminal grammar',()=>{
  for(const name of ['overview','evidence','usage','activity'])assert.match(source,new RegExp(`['\"]${name}['\"]`));
  assert.match(source,/Arguments are not accepted in the terminal prompt/);assert.match(source,/slice\(0, 160\)/);
  assert.match(source,/rows: 30, columns: 88/);assert.match(source,/key: 'fixture-select'/);assert.match(source,/READ ONLY · synthetic fixture/);
});

test('Claude generated fixture artifact exactly matches the authoritative projector',async()=>{
  const generated=fs.readFileSync(path.join(mod,'generated/fixtures.ts'),'utf8');
  const before=generated;
  const {spawnSync}=await import('node:child_process');
  const result=spawnSync(process.execPath,[path.join(root,'scripts/build-claude-mod-fixtures.mjs')],{encoding:'utf8'});
  assert.equal(result.status,0,result.stderr);assert.equal(fs.readFileSync(path.join(mod,'generated/fixtures.ts'),'utf8'),before);
});
