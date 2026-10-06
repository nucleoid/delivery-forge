# Recovery and publication

## Durable local authority

A run manifest and append-only receipts are the execution source of truth. They identify run/repository/plan/base/head/tree, authorization ceiling, ownership generation, worktree, workers/processes, gate receipts, remote state, and next reconciliation requirements. Memory receives distilled outcomes and decisions; it does not replace manifests, leases, Git, process inspection, or remote state.

## Recovery flow

```text
load manifest
   -> inspect local Git/worktree/artifacts/processes
   -> inspect remote branch/PR/checks
   -> inspect/reconcile ownership
   -> reconcile dependencies and authorization
   -> choose safe continuation, pause, or explicit failure
```

Recovery handles commands completing after the last receipt, processes still running, changed local/remote heads, expired/replaced/uncertain leases, dependency contract changes, and evidence produced for another head. No saved checkpoint action is trusted until these facts are reconciled.

## Publication stages

`Local complete`, `PR published`, `CI complete`, `review complete`, `merged`, and `released/deployed` are distinct states. An open PR is not integrated; green CI is not a merge; merge is not deployment.

Before remote mutation, the parent checks ownership, authorization, exact head/tree, required receipts, remote state, and idempotency. If the result is uncertain, stop and inspect rather than blindly retrying a potentially committed operation.

## Rollout and rollback

Plans for running systems explicitly address backward/forward compatibility, configuration/defaults, secret provisioning/rotation without disclosure, migration ordering, reauthentication/permissions, data backfill/resume, observability, rollback feasibility/irreversible boundaries, post-deploy verification, and operator ownership. No stage silently expands into these actions.
