# Architecture

## System boundary

Delivery Forge is a harness around existing agents and engineering tools, not another autonomous agent runtime.

```text
User intent / issue / approved mode
                 |
                 v
       Parent orchestrator
   ┌─────────────┼──────────────┐
   v             v              v
Planner      Scheduler      State/receipts
   |             |              |
   v             v              v
Code + memory  One writer    Local durable
adapters       per issue     authority
                 |
                 v
           Worker contract
                 |
                 v
   RED/GREEN -> affected -> required full/build
       -> complexity/coverage -> mutation -> review
                 |
                 v
      authorized PR / merge / delivery
```

## State machine

1. **Understand** — identify the requested outcome, repository, mode, constraints, and evidence sources.
2. **Resolve** — answer engineering unknowns from evidence; ask one conversational question only for a real user-owned decision.
3. **Plan** — create the shared plan artifact and identify dependencies, serial sections, tests, rollout, and exclusions.
4. **Schedule** — freeze plan revision/base SHA, acquire required ownership, create one writer workspace per issue, and bound expensive-command concurrency.
5. **Implement** — workers change only assigned scope and checkpoint durable state.
6. **Evaluate** — execute layered, capability-aware gates and produce immutable receipts.
7. **Review** — independently review the exact patch/head; assess whether later edits or rebases invalidate review.
8. **Deliver** — perform only the authorized publication stage: none, PR, merge, release, or deployment.
9. **Remember** — store distilled decisions, outcomes, and durable lessons with supersession; keep raw execution evidence in artifacts.

Transitions fail closed when ownership, plan identity, repository head, evidence completeness, or publication state is uncertain.

## Intake and authoring discipline

Minimal interview is the default. Deep interrogation is opt-in. The harness does not ask users to decide ordinary implementation details that repository evidence can settle. When a genuine product, risk, authorization, or irreversible choice remains, it asks one plain question and states its recommendation.

Skill-authoring discipline governs the package itself: keep the entry point thin, load references progressively, use deterministic helpers for repeatable mechanics, and test the contracts. Authoring discipline is not a stage in each delivery run.

## Orchestration and ownership

- Exactly one writer and one isolated worktree own an issue.
- The parent owns queue state, leases, labels/status mirrors, worker dispatch, administrative actions, remote publication, and cleanup.
- Workers cannot publish, merge, weaken policy, approve their own exceptions, or impersonate the lease owner.
- Dependency completion means integrated into the required base, not merely an open pull request.
- Overlapping files, shared interfaces, generated contracts, migrations, and cross-cutting baselines are serialized.
- Independent issues may run concurrently, but expensive command slots are separately bounded by measured machine resources. Agent count is not a resource policy.

## Coordination adapter

The planned coordination adapter assumes an atomic leased-lock service with `acquire_lock`, `renew_lock`, `release_lock`, and `inspect_lock` operations.

- Default lease TTL: 300 seconds.
- Deterministic renewal target: approximately every 100 seconds, using fresh idempotency keys and conservative local deadlines.
- Parent owns lease lifecycle; workers receive delegated scope only.
- Persistent per-resource fencing generations, operation idempotency, and explicit ACLs are required.
- Contention prevents dispatch. Uncertain renewal or ownership stops new work and publication before the last confirmed safe deadline.
- Stale workers and task-owned child processes quiesce before release.
- Status labels are display-only; they are not ownership authority.

A lock does not terminate stale processes or make Git/hosted-service/filesystem writes atomic. External targets must validate fencing for fencing to be meaningful, and many do not. Remote publication therefore remains parent-only with exact-head checks and a documented residual check-to-action race.

The lock contract is an integration prerequisite, not functionality supplied by this planning repository.

## Memory adapter

Use scoped recall first, then fetch complete decision records before relying on them. Check live supersession and applicable scope. Store distilled decisions and outcomes after delivery, including rationale and links to durable evidence.

Do not use memory as a queue, lease, checkpoint store, raw log sink, or source of live process truth. Local manifests and gate receipts remain authoritative for execution.

## Code-intelligence adapter

Begin with a repository brief and existing artifacts. Escalate to symbol search, code search, flow tracing, structural impact, and contract discovery as needed. Every result carries freshness and completeness caveats. Stale, truncated, indexed-only, or heuristic results are checked against the actual checkout. Absence from an index is not proof of absence from the repository.

## Capability detection

Adapters report detected versions, supported operations, configuration, and limitations before their evidence can satisfy a gate. Missing commands, unsupported report formats, incomplete enumeration, timeouts, or absent coverage data become explicit non-pass outcomes.

## Security and portability

No OpenClaw fork or runtime patch is required. Secrets remain outside plans, receipts, logs, repository content, and lock resource keys. Adapters are replaceable and vendor-neutral. Rollout plans explicitly cover compatibility, configuration, secrets, migrations, reauthentication, backfill, rollback, and operator verification where applicable.
