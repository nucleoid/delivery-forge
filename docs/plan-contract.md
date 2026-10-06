# Plan and execution contracts

## Shared plan artifact

A runnable plan contains:

- **Identity:** repository, issue/work item, requested mode, plan revision, plan-content hash, base SHA, author, and creation time.
- **Scope:** requested outcome, included behavior, exclusions, and non-goals.
- **Acceptance criteria:** observable, testable conditions linked to the requested outcome.
- **Decisions:** value, rationale, source (`user`, `repository`, `policy`, or `autonomous`), evidence link, and supersession state.
- **Change map:** exact files, symbols, contracts, schemas, migrations, generators, and expected call/data-flow effects.
- **Dependencies:** prerequisite issues/PRs and the integration condition required before execution.
- **Execution graph:** work units, ownership, serialization groups, overlap declarations, and resource classes.
- **Tests and quality policy:** meaningful RED/GREEN target, affected tests, required full/build gates, complexity/coverage policy, mutation scope, review family, and explicit N/A rationale.
- **Delivery:** authorized publication ceiling and the separate PR/CI/merge/release/deploy conditions.
- **Rollout:** compatibility, configuration, secrets, migrations, reauthentication, backfill, observability, rollback, and operator actions.
- **Unknowns:** remaining uncertainties, evidence needed, owner, and whether each blocks execution.

Before scheduling, freeze the complete plan revision hash and repository base SHA. Any material plan edit creates a new revision. Base drift requires dependency and impact reconciliation before continuing.

## Authorization contract

Authorization is explicit and mode-specific:

| Mode | May change source | May push/open PR | May merge | May deploy/release |
|---|---:|---:|---:|---:|
| `plan` | No | No | No | No |
| `implement` | Yes, within plan | No | No | No |
| `pr` | Yes, within plan | Yes, exact evaluated head | No | No |
| `merge` | Yes, within plan | Yes | Yes, exact reviewed head | No unless separately authorized |
| `resume` | Reconcile first | Preserve previous ceiling | Preserve previous ceiling | Preserve previous ceiling |

The presence of credentials, CLIs, permissions, labels, or an existing branch never widens this ceiling.

## Worker contract

Each worker receives one bounded work unit containing immutable plan identity/base, issue/resource identity, assigned worktree, allowed files/symbols/contracts, exclusions, dependencies/overlaps, acceptance evidence, checkpoint path, stop conditions, and a prohibition on remote publication, policy weakening, lease administration, and unrelated recovery.

Workers checkpoint current head/tree, tracked status, running child processes, commands/results, artifacts, limitations, and next safe action. A checkpoint is evidence of state, not authorization to continue blindly.

## Scheduling contract

One active writer/worktree owns an issue. Dispatch waits for all required resources. Multiple resources are acquired in deterministic global order, with partial acquisition released before retry. Open PRs remain pending integration. Shared contracts, overlap, migrations, baselines, and generated artifacts serialize. Build/test/mutation slots are bounded independently from agent count using measured resources. Parent alone performs queue changes, worker replacement, remote status mutation, publication, and cleanup.

## Pause, stop, and resume

Pause/stop halts dispatch/publication, quiesces task-owned processes, records Git/process/lease/evidence state, and preserves the worktree unless cleanup is explicitly authorized. Do not release ownership while stale work can still mutate shared state.

Resume never blindly runs a saved next command. It reconciles checkpoint schema, worktree and artifacts, Git head/tree/status, base/remote branch/PR/check state, live processes, ownership generation/deadline, dependency integration, changed contracts, and the previous authorization ceiling before selecting a safe action.
