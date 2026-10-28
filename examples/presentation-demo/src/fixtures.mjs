import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { usageForScenario } from './usage.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
export const fixturePath = path.resolve(here, '../data/fixtures.json');

const clone = (value) => structuredClone(value);
const merge = (base, patch) => {
  if (Array.isArray(patch) || patch === null || typeof patch !== 'object') return clone(patch);
  const out = { ...(base && typeof base === 'object' ? clone(base) : {}) };
  for (const [key, value] of Object.entries(patch)) out[key] = merge(out[key], value);
  return out;
};

function gate(id, label, outcome, head, tree, extra = {}) {
  return {
    id, label, required: true, outcome, currentness: 'current',
    receiptId: `receipt-${id}-${head.slice(0, 7)}`,
    headCommit: head, treeId: tree, policyIdentity: 'policy-demo-safe-v1',
    artifactRefs: [`fixture://evidence/${id}.txt`], limitations: [],
    notApplicableRationale: null, notApplicableAllowed: false, ...extra
  };
}

function expandMacro(value, snapshot) {
  if (value === '$exactHeadPass') {
    const h = snapshot.run.headCommit, t = snapshot.run.treeId;
    return [gate('build','Build','PASS',h,t), gate('tests','Tests','PASS',h,t), gate('crap4csharp','Crap4CSharp','PASS',h,t), gate('mutation','Mutate4CSharp','PASS',h,t), gate('migration','Migration','NOT_APPLICABLE',h,t,{notApplicableAllowed:true,notApplicableRationale:'No schema or data migration in approved scope.'})];
  }
  if (value === '$staleOldHead') {
    const h='c82e4a100000000000000000000000000000003', t='tree-demo-c82e4a1000000000000000000000000002';
    return ['build','tests','crap4csharp','mutation'].map(id => gate(id, id === 'crap4csharp' ? 'Crap4CSharp' : id === 'mutation' ? 'Mutate4CSharp' : id[0].toUpperCase()+id.slice(1), 'PASS', h, t, { currentness:'stale' }));
  }
  if (value === '$failVariant') {
    const h=snapshot.run.headCommit,t=snapshot.run.treeId;
    return [gate('build','Build','PASS',h,t),gate('tests','Tests','FAIL',h,t,{limitations:['CSV quote escaping assertion failed']})];
  }
  if (value === '$errorVariant') {
    const h=snapshot.run.headCommit,t=snapshot.run.treeId;
    return [gate('build','Build','PASS',h,t),gate('tests','Tests','ERROR','wrong-head-00000000000000000000000000000000',t,{limitations:['Receipt head does not bind to snapshot head']})];
  }
  return value;
}

export function loadCatalog(file = fixturePath) {
  const bytes = fs.statSync(file).size;
  if (bytes > 262144) throw new Error('fixture exceeds 256KiB');
  const catalog = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (catalog.demoOnly !== true) throw new Error('fixture catalog must be demo-only');
  return catalog;
}

export function snapshotsFromCatalog(catalog = loadCatalog()) {
  let current = clone(catalog.baseSnapshot);
  return catalog.events.map((event) => {
    current = merge(current, event.data);
    current.source.sequence = event.sequence;
    current.source.observedAt = event.observedAt;
    current.source.receivedAt = current.source.receivedAt || event.observedAt;
    current.gates = expandMacro(current.gates, current);
    current.usage = { fixtureScenario: event.name };
    return { name: event.name, event: clone(event), snapshot: clone(current) };
  });
}

export function getScenario(name, catalog = loadCatalog()) {
  const found = snapshotsFromCatalog(catalog).find((x) => x.name === name);
  if (!found) throw new Error(`unknown fixture scenario: ${name}`);
  return found;
}

export { merge };
