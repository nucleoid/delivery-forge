# Planning core

`DeliveryForge.Planning` turns bounded intake and evidence into a complete frozen plan. It does not
call an agent runtime, model, memory service, code index, Git host, or publication API.

## Decision delta: typed portable boundary

The planning bundle uses a narrow producer-aware boundary. This deliberately supersedes the earlier
curl, shell, YAML, JSON, Dockerfile, Compose, and Kubernetes parsing contract. Delivery Forge does
not infer commands from arbitrary documents and does not claim that scanning arbitrary text proves
it public-safe.

Portable evidence records:

- source kind and one public producer kind (`explicitUserPublic`, `repositoryAtBase`, or
  `deterministicGenerated`);
- a public locator;
- optional immutable SHA-256 digest, UTC observation time, completeness and freshness caveats;
- optional supersession; and
- exact checkout commit/tree plus symlink and generation metadata for repository-at-base evidence.

Private advisory records remain in local intake and are absent from public provenance in their
entirety: source kind, producer kind, locator, digest, summary, supersession, and nested metadata are
not projected. The planner emits typed caveat, notice, and meaning codes with deterministic fixed
sentences. A private-advisory producer cannot become public through an opaque prefix, a caller label,
or an identity conversion. Required advisory meaning that has no safe typed distillation blocks
readiness; optional undistilled meaning produces a fixed typed caveat.

Public evidence has separate construction paths. Repository facts come only from reader-issued exact
Git objects, deterministic policy facts use their own factory, and explicitly public user intent uses
a deliberate host-authority factory. The library prevents its supported local-advisory/import path
from exporting private records, but it cannot prove the byte history or secret absence of arbitrary
explicit-public prose and cannot defeat a malicious host that deliberately relabels private text as
new user-public input. Hosts must expose that path only for genuinely explicit public intent.

## Intake and readiness

Minimal intake is the default. A genuine product, scope, risk, or authority choice is represented as
one single-line user-owned decision paired with one recommended option, and blocks readiness with one
conversational question. Deep intake is explicit and requires distinct complete pinned evidence
beyond minimal intake. Aliases are deduplicated by locator and canonical digest/commit/tree identity.
Only fresh, complete, non-heuristic imported repository evidence verified against exact checkout
bytes can satisfy the imported deep-evidence path.

Unavailable or empty optional context produces an honest fixed limitation. Required missing context,
required incomplete provenance, checkout conflict, or required private meaning that cannot be safely
distilled blocks readiness. A valid envelope or successful defense-in-depth scan is not intake
approval, plan approval, execution authority, or publication authority.

## Typed commands

A gate command is an executable token plus typed argument tokens:

- `literal` for non-secret values;
- `placeholder` for a named non-secret value supplied later; and
- `secretReference` for a named secret supplied by a future executor.

Real secret values are invalid in portable arguments. The planning bundle includes the typed command
and a deterministic readable display string because downstream issue-3 gate receipts retain a
command-string field. The display string is an adapter only; it is not shell parsing or execution.

## Defense in depth

Typed public values receive bounded checks for NUL/control characters, common private host paths,
home aliases, populated credential assignments, private-key markers, bearer values, and a few
high-confidence token formats. These checks catch common mistakes and may have false positives or
false negatives. They are not a privacy certificate and do not define an arbitrary grammar. Private
advisory isolation is enforced by producer/API separation, not substring, normalized, fuzzy,
case-folded, whitespace, or encoding comparisons against imported text.

## Exact repository boundary

`GitRepositoryContextReader` resolves exact commit and tree objects and reads blobs by object ID.
It separately binds mutable checkout observations, uses finite process and traversal limits, disables
replace refs, lazy fetch and transport, validates included promisor configuration, walks tree segments
exactly, and resolves symlinks conservatively under Linux and Windows lexical semantics. Shallow
history, submodules, generated files, dirty state, detached HEAD, unresolved links, and bound
exhaustion remain explicit caveats.

Repository evidence is created from a reader-issued `RepositoryFile`; freeze rejects forged,
mutated, or base-mismatched commit/tree and safety bindings. Post-verification imported-record
mutation downgrades verification to untrusted.

## Complete plan and freeze

A ready draft includes intent and requested ceiling, scope, acceptance criteria, provenance, exact
change map, acyclic dependency graph, typed gates, rollout assessments, and owned unknowns. Missing
required sections, blocking unknowns, invalid ceilings, or an intake assessment bound to different
inputs prevents freeze.

Freeze sorts set-like inputs, rejects duplicate semantic keys, computes a stable material digest,
and embeds it in the strict issue-3 `plan@1.0.0` `planRevision`. The issue-3 schema remains unchanged;
typed evidence and commands live in the separately identified JCS-canonical planning bundle.
Semantically equivalent reordering is reproducible. Material edits require a new declared revision
and record contract supersession. Reconciliation accepts only a reader-issued context for the same
verified `refs/heads/*` reference; commit/tree drift invalidates downstream readiness.

Validation establishes shape and identity only. It grants no approval, publication, merge, release,
deployment, configuration, credential, or installation authority.

## Platforms

Linux and Windows are v1 targets. Local evidence establishes Linux behavior; Windows requires its
hosted CI lane before merge. macOS is unclaimed.
