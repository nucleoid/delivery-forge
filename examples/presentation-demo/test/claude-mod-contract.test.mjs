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
  assert.deepEqual([...new Set(calls)].sort(),['command.register','store.get','store.set','ui.close','ui.invalidate','ui.open','ui.resolve']);
});

test('Claude mod exposes all four views and bounded synthetic fixture commands',()=>{
  for(const name of ['overview','evidence','usage','activity'])assert.match(source,new RegExp(`['\"]${name}['\"]`));
  for(const command of ['open','view','scenario','reset','close','help'])assert.match(source,new RegExp(`['\"]${command}['\"]`));
  assert.match(source,/Read-only synthetic fixture/);assert.match(source,/slice\(0, 160\)/);
});
