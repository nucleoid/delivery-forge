import crypto from 'node:crypto';
import { validateSnapshot } from './projector.mjs';

const ACTIONS = new Set(['request.pause','request.stop','request.resume','request.repair','request.publish-pr']);
export function makeIntent(snapshot, action, now = '2026-10-19T10:15:00.000Z') {
  return {
    schemaVersion:'forge.action/0.1', intentId:`preview-${crypto.createHash('sha256').update(`${snapshot.source.snapshotId}:${action}`).digest('hex').slice(0,12)}`,
    action, runId:snapshot.run.id, expectedSnapshotId:snapshot.source.snapshotId, expectedSequence:snapshot.source.sequence,
    expectedPlanIdentity:snapshot.run.planIdentity, expectedPlanRevision:snapshot.run.planRevision,
    expectedHeadCommit:snapshot.run.headCommit, expectedTreeId:snapshot.run.treeId,
    expectedPolicyIdentity:snapshot.run.policyIdentity, expectedCapabilityIdentity:snapshot.source.capabilityIdentity,
    requestedCeiling: action === 'request.publish-pr' ? 'pr' : snapshot.run.authorizationCeiling,
    approvalRequestRef:null, idempotencyKey:`demo:${snapshot.run.id}:${snapshot.source.sequence}:${action}`,
    issuedAt:now, expiresAt:'2026-10-19T10:20:00.000Z', payload:{scope:'fixture-only'}
  };
}

export function previewIntent(snapshot, intent) {
  const reject = (reason) => ({status:'REJECTED',reason,sideEffects:0,authorityIssued:false});
  const snapshotErrors=validateSnapshot(snapshot);
  if(snapshotErrors.length) return reject(`invalid snapshot: ${snapshotErrors[0]}`);
  if (!intent || intent.schemaVersion !== 'forge.action/0.1') return reject('unsupported intent schema');
  if (!ACTIONS.has(intent.action)) return reject('action is not in the closed consequential enum');
  if (snapshot.mode !== 'demo' || snapshot.source.realm !== 'fixture') return reject('prototype accepts demo fixture snapshots only');
  const bindings = [
    ['run',intent.runId,snapshot.run.id],['snapshot',intent.expectedSnapshotId,snapshot.source.snapshotId],['sequence',intent.expectedSequence,snapshot.source.sequence],
    ['plan',intent.expectedPlanIdentity,snapshot.run.planIdentity],['plan revision',intent.expectedPlanRevision,snapshot.run.planRevision],
    ['head',intent.expectedHeadCommit,snapshot.run.headCommit],['tree',intent.expectedTreeId,snapshot.run.treeId],
    ['policy',intent.expectedPolicyIdentity,snapshot.run.policyIdentity],['capability',intent.expectedCapabilityIdentity,snapshot.source.capabilityIdentity]
  ];
  const mismatch = bindings.find(([,actual,expected]) => actual !== expected);
  if (mismatch) return reject(`${mismatch[0]} binding mismatch`);
  if (snapshot.source.freshness !== 'current' || snapshot.source.continuity !== 'complete') return reject('reconciliation required for stale or discontinuous source');
  if (snapshot.workflow.cleanup === 'unknown') return reject('cleanup state is unknown');
  const advertised = snapshot.actions.find((a)=>a.id===intent.action);
  if (!advertised?.available) return reject(advertised?.reason || 'action unavailable in this snapshot');
  return {status:'PREVIEW_ONLY',action:intent.action,intent,sideEffects:0,authorityIssued:false,note:'No controller adapter exists in this prototype.'};
}

export function replayAction(name) {
  if (!['demo.next','demo.prev','demo.reset'].includes(name) && !name.startsWith('demo.scenario.')) throw new Error('invalid demo replay action');
  return {namespace:'demo',action:name,sideEffects:0,authorityIssued:false};
}
