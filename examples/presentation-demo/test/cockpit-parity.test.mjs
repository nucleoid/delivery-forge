import test from 'node:test';
import assert from 'node:assert/strict';
import { snapshotsFromCatalog,getScenario } from '../src/fixtures.mjs';
import { cockpitView,linesForView } from '../src/cockpit-view-model.mjs';
import { displayWidth,sliceByColumns,truncateToWidth } from '../src/safety.mjs';

test('shared native view model exposes honest four-view facts for every fixture',()=>{
  const history=snapshotsFromCatalog();
  for(const [i,state] of history.entries()){
    const vm=cockpitView(state.snapshot,{history:history.slice(0,i+1)});
    assert.match(vm.header.join('\n'),/SYNTHETIC/);assert.match(vm.header.join('\n'),new RegExp(vm.snapshot.aggregate.outcome));
    for(const view of ['overview','evidence','usage','activity'])assert.ok(linesForView(state.snapshot,view,{history:history.slice(0,i+1)}).length>0);
    if(vm.snapshot.aggregate.outcome!=='PASS')assert.doesNotMatch(vm.header.slice(0,4).join('\n'),/✓ PASS/);
  }
});

test('usage and receipt detail preserve durable identities and unknown pricing',()=>{
  const state=getScenario('failed-gate'),vm=cockpitView(state.snapshot,{selected:3,history:[state]});
  assert.match(vm.detail.join('\n'),/receipt-mutation-attempt-1/);assert.match(vm.detail.join('\n'),new RegExp(vm.snapshot.run.headCommit));assert.match(vm.detail.join('\n'),/Read-only/);
  const usage=vm.usage.join('\n');assert.match(usage,/USD API-EQUIVALENT ESTIMATE/);assert.match(usage,/UNPRICED: effective model and pricing SKU unresolved/);assert.match(usage,/not a bill/);assert.match(usage,/Ledger identity unavailable/);assert.doesNotMatch(usage,/demo-ledger-/);assert.match(usage,/Observed requests 4 · priced 3 · unpriced 1 · final 4 · provisional 0/);assert.match(usage,/VALUATION: FINAL/);assert.match(usage,/provider-final r2/);assert.match(usage,/cache read observed \/ write observed/);assert.doesNotMatch(usage,/actual charge: 0/);
  assert.match(vm.overview.join('\n'),/requirements: build, tests, crap4csharp, mutation, migration/);assert.match(vm.overview.join('\n'),/reason: Mutate4CSharp: incomplete/);
});

test('display-cell slicing preserves graphemes and bounds CJK, combining text, emoji and ANSI',()=>{
  for(const value of ['漢字 café','e\u0301vidence','🧪 PASS','\x1b[31m× FAIL\x1b[0m'])for(const width of [4,8,12])assert.ok(displayWidth(truncateToWidth(value,width))<=width);
  assert.equal(sliceByColumns('漢字x',4),'漢字');assert.equal(sliceByColumns('e\u0301x',1),'e\u0301');
});

test('valuation follows final versus provisional observations and cache metadata stays explicit',()=>{
  const execute=cockpitView(getScenario('execute').snapshot).usage.join('\n');assert.match(execute,/VALUATION: PROVISIONAL/);assert.match(execute,/provisional 1/);
  const complete=cockpitView(getScenario('failed-gate').snapshot).usage.join('\n');assert.match(complete,/VALUATION: FINAL/);assert.match(complete,/cache read observed \/ write observed/);
});
