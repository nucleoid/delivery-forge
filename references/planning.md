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
does not pretend the plan is ready.

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
bytes and are marked when they escape the checkout. Generated-file classification is deliberately
conservative: known path/name conventions are marked, while all other files say that generator
metadata was not asserted. Missing refs and objects fail rather than falling back to worktree
bytes.

## Complete plan and freeze

A ready `PlanDraft` includes:

- intent, requested authorization ceiling, included/excluded scope, and acceptance criteria;
- provenance and exact change targets;
- an acyclic dependency graph with integration conditions;
- gates/tests and expected outcomes;
- compatibility, configuration, secrets, migration, reauthentication, backfill, observability,
  rollback, and operator-action assessments;
- owned unknowns, with no unresolved blocking unknown.

Freeze normalizes set-like inputs, validates completeness and portability, and creates two linked
identities. `contractIdentity` is a strict `plan@1.0.0` projection validated by
`DeliveryForge.Contracts.ContractValidator`. The complete `planning-bundle` is independently
strict-parsed, JCS-canonicalized, and SHA-256 identified with the same contract library. This keeps
the issue #3 schema stable while preserving all issue #4 sections in the frozen artifact.

Material edits or a revision change produce a new identity. A frozen commit/tree mismatch marks
downstream readiness false until reconciliation. Validation proves shape and identity only; it is
not approval, execution, publication, or merge authority.

## Platform boundary

The library targets Linux and Windows through `net10.0`. Local tests establish Linux behavior.
Windows support is a hosted-CI claim only after that lane passes. macOS is not claimed for v1.
