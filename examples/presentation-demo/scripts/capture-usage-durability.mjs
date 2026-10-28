#!/usr/bin/env node
import { appendFileSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { appendOwnedUsageFixture, initializeOwnedUsageJsonl, replayOwnedUsageJsonl } from '../src/usage-jsonl.mjs';

const dir=mkdtempSync(join(tmpdir(),'forge-owned-usage-evidence-')),path=join(dir,'owned-synthetic.jsonl');
try{
 initializeOwnedUsageJsonl(path);
 for(const name of ['writerPartial','writerFinal','reviewerFinal','writerRetry','unresolved','writerFinal'])appendOwnedUsageFixture(path,name);
 const healthy=replayOwnedUsageJsonl(path),before=readFileSync(path);
 appendFileSync(path,'{malformed}\n{"schemaVersion":');
 const damagedBytes=readFileSync(path),damaged=replayOwnedUsageJsonl(path),after=readFileSync(path);
 const output=[
  'HARNESS EVIDENCE — owned synthetic JSONL durability; no installed host or provider',
  `healthy: USD ${healthy.summary.knownCostUsd} | tokens ${healthy.summary.observedTokens} | ${healthy.summary.status} | records ${healthy.recordCount}`,
  `duplicate append unchanged: ${healthy.summary.knownCostUsd==='0.044225'&&healthy.summary.observedTokens===19300}`,
  `malformed/truncated: ${damaged.summary.status} | ${damaged.summary.conditions.join('; ')}`,
  `reader mutated damaged bytes: ${!damagedBytes.equals(after)}`,
  `pre-damage bytes retained as prefix: ${damagedBytes.subarray(0,before.length).equals(before)}`,
  'actual charge: unknown; production collector: absent'
 ].join('\n')+'\n';
 writeFileSync(resolve(dirname(fileURLToPath(import.meta.url)),'../../evidence/usage-jsonl-durability.txt'),output);
 process.stdout.write(output);
}finally{rmSync(dir,{recursive:true,force:true});}
