# Planning core

`DeliveryForge.Planning` turns bounded intake and evidence into a complete, frozen plan without
calling an agent runtime, model, memory service, code index, MCP server, Git host, or publication
API. The host may collect context, but the .NET core accepts only explicit values and local Git
repository reads.

## Intake

Minimal intake is the default. Ordinary engineering choices should be resolved from pinned
repository or policy evidence. Deep intake is enabled only when the caller explicitly sets
`IntakeDepth.Deep`. A genuine product, scope, risk, or authority choice is represented as one
user-owned decision; assessment returns one conversational question with a recommendation and
does not pretend the plan is ready. The resulting `IntakeAssessment` is a required part of the
`PlanDraft`; freeze rechecks it, the requested depth, unresolved user-owned decisions, and required
imported-context availability rather than trusting an unrelated earlier intake call.

Unavailable optional imported context records a limitation and does not block repository-driven
planning. If the caller declares that context required, its absence blocks. Empty imported search
results are not evidence that corresponding repository behavior is absent.

## Provenance and repository context

Every `EvidenceItem` records source kind, locator, immutable digest when available, observation
time, completeness, caveats, and optional supersession. Raw memory/index content should not be
placed in an evidence item.

`GitRepositoryContextReader` resolves a requested ref to exact commit and tree objects and reads
file bytes by blob object ID. Dirty state and detached HEAD are separate mutable observations.
Shallow history and submodules remain explicit limitations. Symlink targets are returned as blob
bytes; in-tree chains are resolved from committed tree/blob objects using portable slash semantics,
with escape, cycle, and missing-target outcomes kept distinct. Git reads have a finite deadline,
terminate the process tree on overflow or cancellation, disable lazy fetch, optional locks, and
fsmonitor side effects, and ignore inherited `GIT_DIR`/`GIT_WORK_TREE` routing. Generated-file
classification is deliberately conservative: known path/name conventions are marked, while all
other files say that generator metadata was not asserted. Missing refs and objects fail rather
than falling back to worktree bytes.

## Complete plan and freeze

A ready `PlanDraft` includes:

- intent, requested authorization ceiling, included/excluded scope, and acceptance criteria;
- provenance and exact change targets;
- an acyclic dependency graph with integration conditions;
- gates/tests and expected outcomes;
- compatibility, configuration, secrets, migration, reauthentication, backfill, observability,
  rollback, and operator-action assessments;
- owned unknowns, with no unresolved blocking unknown.

Freeze normalizes set-like inputs, rejects duplicate semantic keys, validates completeness and
platform-neutral portability, and creates two linked identities. A canonical digest covers the
complete material plan (intake evidence, provenance, exact change map, DAG, gates, rollout,
unknowns, repository observations, and retained limitations). The strict issue-#3-compatible
`plan@1.0.0` contract uses a `planRevision` deterministically bound to that digest, so its
`contractIdentity` changes whenever material substance changes. `FrozenPlan.PlanContractBytes`
returns the canonical validated contract bytes for downstream reference validation; consumers do
not reconstruct a private projection. The complete `planning-bundle` is independently
JCS-canonicalized and SHA-256 identified.

Supplying a predecessor rejects reuse of the same declared revision for different content and
records supersession when a new revision changes substance. A frozen commit/tree mismatch marks
downstream readiness false until reconciliation. Validation proves shape and identity only; it is
not approval, execution, publication, or merge authority.

## Platform boundary

The library targets Linux and Windows through `net10.0`. Local tests establish Linux behavior.
Windows support is a hosted-CI claim only after that lane passes. macOS is not claimed for v1.
