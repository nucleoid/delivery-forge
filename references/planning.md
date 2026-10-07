# Planning core

`DeliveryForge.Planning` turns bounded intake and evidence into a complete, frozen plan without
calling an agent runtime, model, memory service, code index, MCP server, Git host, or publication
API. The host may collect context, but the .NET core accepts only explicit values and local Git
repository reads.

## Intake

Minimal intake is the default. Ordinary engineering choices should be resolved from pinned
repository or policy evidence. Deep intake is enabled only when the caller explicitly sets
`IntakeDepth.Deep`; it requires at least one additional distinct complete evidence item beyond
the evidence sufficient for minimal intake, and the complete intake is capped at 256 items. The
host collects that evidence through the same repository, policy, and imported-context contracts;
the .NET core does not call host tools. A genuine product, scope, risk, or authority choice is
represented as one user-owned decision paired with a concrete recommended option; assessment
returns one conversational question naming that option and does not pretend the plan is ready.
The resulting `IntakeAssessment` is a required part of the
`PlanDraft`; freeze rechecks its deterministic binding to the request, evidence, imported-context
requirement/availability, and retained caveats, as well as unresolved user-owned decisions. An
assessment from an unrelated earlier intake call cannot be substituted.

Unavailable or empty optional imported context records a limitation and does not block
repository-driven planning. If the caller declares that context required, absence or an empty
result blocks. Empty imported search results are not evidence that corresponding repository
behavior is absent.

## Provenance and repository context

Every `EvidenceItem` records source kind, locator, immutable digest when available, observation
time, completeness, an explicit optional/required requirement, caveats, and optional
supersession. Incomplete optional evidence remains in the plan with its caveats; incomplete
required evidence blocks both intake and freeze. At least one complete repository or policy item
is always required for readiness. Raw memory/index content should not be placed in an evidence
item.

`GitRepositoryContextReader` resolves a requested ref to exact commit and tree objects and reads
file bytes by blob object ID. Dirty state and detached HEAD are immutable snapshots of mutable
observations. Repository contexts are reader-issued, defensively copied, and bound across commit,
tree, root, requested ref, submodule gitlinks, limitations, and every mutable observation; freeze
and file reads reject an unbound or altered context. Shallow history and submodules remain explicit
limitations. Submodule gitlinks are read from the parent commit without launching Git in submodule
worktrees. Symlink targets are returned as blob bytes; in-tree chains are resolved from committed
tree/blob objects using both POSIX expansion and conservative Windows text-collapse semantics.
Resolution has one wall deadline plus global hop, segment, target-size, and Git-invocation budgets.
Escape is reported when either platform rule escapes, and cycle, missing, or bound-exceeded outcomes
are conservatively marked as potentially escaping. Git reads have a finite deadline,
terminate the process tree on overflow or cancellation, disable replace refs, lazy fetch, optional
locks, and fsmonitor side effects, force literal pathspecs, and remove inherited repository,
object, index, common-directory, and config routing. Effective included configuration across
repository, worktree, global, and system scopes is inspected fail closed for partial-clone/promisor
settings. All Git transport protocols are disabled for these local reads, providing a second
no-network boundary on Git versions earlier than 2.44 where `GIT_NO_LAZY_FETCH` is unavailable;
ordinary full clones remain supported. Generated-file
classification is deliberately conservative: known path/name conventions are marked, while all
other files say that generator metadata was not asserted. Missing refs and objects fail rather
than falling back to worktree bytes.

`RepositoryFile` instances are created only by `GitRepositoryContextReader` and carry the exact
commit and tree from which their blob was read. Imported-context verification binds locator,
digest, verification status, commit, and tree. The identity is retained in limitations, and freeze
rejects verified imported context whose commit or tree differs from the draft base.

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
stable material plan (intake evidence, provenance, exact change map, DAG, gates, rollout,
unknowns, and intake limitations). Mutable repository observations and their limitations remain
in the frozen bundle for honesty but do not alter the declared revision's content digest. The strict issue-#3-compatible
`plan@1.0.0` contract uses a `planRevision` deterministically bound to that digest, so its
`contractIdentity` changes whenever material substance changes. `FrozenPlan.PlanContractBytes`
returns the canonical validated contract bytes for downstream reference validation; consumers do
not reconstruct a private projection. The complete `planning-bundle` is independently
JCS-canonicalized and SHA-256 identified.

Supplying a predecessor rejects reuse of the same declared revision for different stable content
and records supersession when a new revision changes substance. Frozen-plan properties are
get-only; base reconciliation returns a new immutable view whose downstream readiness is false
without permitting identity, base, or readiness to diverge from canonical bytes. Validation proves
shape and identity only; it is not approval, execution, publication, or merge authority.

## Platform boundary

The library targets Linux and Windows through `net10.0`. Local tests establish Linux behavior.
Windows support is a hosted-CI claim only after that lane passes. macOS is not claimed for v1.
