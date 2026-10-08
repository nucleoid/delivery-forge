# Planning core

`DeliveryForge.Planning` turns bounded intake and evidence into a complete, frozen plan without
calling an agent runtime, model, memory service, code index, MCP server, Git host, or publication
API. The host may collect context, but the .NET core accepts only explicit values and local Git
repository reads.

## Intake

Minimal intake is the default. Ordinary engineering choices should be resolved from pinned
repository or policy evidence. Deep intake is enabled only when the caller explicitly sets
`IntakeDepth.Deep`; it requires at least one additional distinct complete evidence item beyond
the evidence sufficient for minimal intake, and counts only pinned repository/policy evidence or
entries carried in the bounded imported-context envelope. Eligible items are counted once across both
inputs by locator and by canonical bound identity/digest, so aliases of the same bytes cannot satisfy
the threshold twice. Imported items count only when they are distinct, verified against exact checkout
bytes, non-memory, fresh, complete, and non-heuristic. User, raw memory, stale, truncated, heuristic,
unverified, and duplicate claims do not satisfy that threshold. The complete intake is capped at 256 items. The
host collects that evidence through the same repository, policy, and imported-context contracts;
the .NET core does not call host tools. A genuine product, scope, risk, or authority choice is
represented as one single-line, non-blank user-owned decision paired with one single-line concrete recommended option. Control
characters and Unicode line/paragraph separators are rejected while ordinary single-line Unicode prose is retained; assessment
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
required evidence blocks both intake and freeze. Readiness requires at least one complete pinned
policy item or repository item created from an exact `RepositoryFile` with
`EvidenceItem.FromRepositoryFile`; a digestless item cannot establish readiness. Raw memory/index
content should not be placed in an evidence item. A `git:` locator is repository evidence only,
cannot be relabeled as policy or imported readiness evidence, and must use forward slashes rather
than a normalized backslash alias.

`GitRepositoryContextReader` resolves a requested ref to exact commit and tree objects, walks each
tree object segment by segment with exact entry-name matching, and reads file bytes by blob object ID.
Dirty state and detached HEAD are immutable snapshots of mutable
observations bound to the separately recorded checkout HEAD commit/tree. When requested objects
differ from checkout HEAD, the context says explicitly that those observations do not describe the
requested base. Repository contexts are reader-issued, defensively copied, and bound across commit,
tree, root, requested ref, submodule gitlinks, limitations, and every mutable observation; freeze
and file reads reject an unbound or altered context. Shallow history and submodules remain explicit
limitations. Submodule gitlinks are read from the parent commit without launching Git in submodule
worktrees. Symlink targets are returned as blob bytes; in-tree chains are resolved from committed
tree/blob objects under both POSIX expansion and conservative Windows lexical-collapse semantics.
POSIX parsing treats backslash as a literal filename character; Windows lexical parsing treats it as
a separator. Resolution caches each distinct tree listing across both semantic passes and charges that Git work once,
with one wall deadline plus global hop, segment, target-size, and Git-invocation budgets.
Escape is reported when either platform rule escapes, and cycle, missing, or bound-exceeded outcomes
are conservatively marked as potentially escaping. Git reads have a finite deadline,
terminate the process tree on overflow or cancellation, disable replace refs, lazy fetch, optional
locks, and fsmonitor side effects, avoid pathspec-based file resolution, and remove inherited repository,
object, index, common-directory, and config routing. The Git executable is
resolved once to an absolute file from explicit configuration or absolute `PATH` entries; repository
and current-directory executable search is never used. Effective included configuration across
repository, worktree, global, and system scopes is inspected fail closed for partial-clone/promisor
settings. All Git transport protocols are disabled for these local reads, providing a second
no-network boundary on Git versions earlier than 2.44 where `GIT_NO_LAZY_FETCH` is unavailable;
ordinary full clones remain supported. Generated-file
classification is deliberately conservative: known path/name conventions are marked, while all
other files say that generator metadata was not asserted. Missing refs and objects fail rather
than falling back to worktree bytes.

`RepositoryFile` instances are created only by `GitRepositoryContextReader` and carry the exact
commit and tree from which their blob was read. Repository evidence used for readiness is created
from that file, binding its `git:` locator, digest, commit, tree, symlink disposition, and generated-file
classification. Unsafe/unresolved symlinks and conventionally generated files remain provenance with
explicit caveats but do not establish clean readiness, including when imported context is verified against
their exact bytes. Imported verification binds and retains the reader-issued symlink disposition and generated-file
classification. Freeze preserves this metadata in canonical output
including each repository item's commit/tree, and rejects every altered or base-mismatched binding,
including incomplete unsafe/generated evidence. Imported-context verification binds kind, locator,
summary, digest, observed time, stale/truncated/heuristic flags, claimed checkout digest,
verification status, commit, and tree. Mutation of any bound field downgrades the record to unverified.
The identity is retained in limitations, and freeze
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

Supplying a predecessor requires matching repository/work-item lineage, rejects reuse of the same
declared revision for different stable content, and directly records supersession whenever the
contract identity changes (including a declared-revision-only change). Frozen-plan properties are
get-only; base reconciliation accepts only a reader-issued `RepositoryContext` resolving the same
freshness-bearing mutable ref. Only an exact valid full `refs/heads/*` refname whose existence and
resolved object were verified in its reader-bound provenance is freshness-bearing on both sides;
`HEAD`, short branch names, pinned objects, and revision/reflog expressions are snapshots for reconciliation purposes.
An exact full branch ref request is reconciled by its resolved commit/tree even when checkout HEAD is detached;
checkout detachment does not turn that requested branch into a snapshot. A pinned commit, tag, detached `HEAD`
snapshot, or differently requested ref cannot prove that the original branch did not drift. Reconciliation returns a new immutable view whose downstream readiness is false
without permitting identity, base, or readiness to diverge from canonical bytes. Validation proves
shape and identity only; it is not approval, execution, publication, or merge authority.

## Platform boundary

Portable gate commands may use ordinary `./`, `../`, `.\\`, and `..\\` repository-relative arguments. Absolute POSIX,
Windows, UNC, home-alias, arbitrary rooted-Windows, and option-attached private host paths remain rejected.
Portable plan and imported-context text also reject schemeless curl proxy userinfo across grouped
short flags and proxy-taking long options, plus every non-empty `Authorization` header value, while
preserving noncredential proxy arguments, empty `Authorization` headers, and ordinary headers. Curl
credential scanning is escape-aware and command-position-aware: quoted option values do not become
prose or later-command boundaries, while actual later Docker, Podman, and Kubernetes commands do.
Bounded JSON exec arrays and YAML/Docker/Kubernetes command sequences are scanned across lines; text
outside those explicit structures is never joined. Backslash, PowerShell-backtick, cmd-caret, and
doubled-quote key escapes are normalized only within their physical line. A single-backslash curl
alias remains supported without masking drive, UNC/rooted, or home-alias private paths.

The library targets Linux and Windows through `net10.0`. Local tests establish Linux behavior.
Windows support is a hosted-CI claim only after that lane passes. macOS is not claimed for v1.
