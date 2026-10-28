#!/usr/bin/env node
import { appendOwnedUsageFixture, initializeOwnedUsageJsonl, replayOwnedUsageJsonl } from '../src/usage-jsonl.mjs';

const [command,path,eventName,...rest]=process.argv.slice(2);
if(rest.length||!['init','append','replay'].includes(command)||!path||(command==='append'&&!eventName)||(command!=='append'&&eventName)){
 process.stderr.write('usage: node scripts/usage-ledger.mjs init <explicit-path> | append <explicit-path> <bundled-event-name> | replay <explicit-path>\n');
 process.exitCode=2;
}else if(command==='init'){
 initializeOwnedUsageJsonl(path);process.stdout.write('initialized owned synthetic JSONL ledger\n');
}else if(command==='append'){
 appendOwnedUsageFixture(path,eventName);process.stdout.write(`appended bundled synthetic fixture ${eventName}\n`);
}else{
 const result=replayOwnedUsageJsonl(path);
 process.stdout.write(`${JSON.stringify({schemaVersion:result.summary.schemaVersion,synthetic:true,status:result.summary.status,knownCostUsd:result.summary.knownCostUsd,observedTokens:result.summary.observedTokens,unpricedRequests:result.summary.unpricedRequests,conditions:result.summary.conditions,recordCount:result.recordCount,mutated:false},null,2)}\n`);
}
