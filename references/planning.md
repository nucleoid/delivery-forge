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

## GitHub issue planning and handoff

A requested GitHub issue plan includes durable delivery back to that issue unless
the user explicitly requests a local/chat-only draft. This is planning
metadata, not permission to implement, create a PR or merge. The skill's agent
performs the GitHub reads/comment/label operations; the deterministic planning
core only freezes the bundle and does not call GitHub.

### Read the actual issue and prepare planning inputs

Prefer an existing working `gh` CLI. Use an explicit repository and read the body,
comments, labels and state, for example:

```powershell
gh issue view <N> --repo <owner>/<repo> --json number,title,state,body,comments,labels,url
```

An actual successful read establishes access. Merely finding `gh` does not prove
it is authenticated, and a separate connector's 404 does not establish that an
issue is absent. If the selected route fails, report its actual error and use
another already-authorized route where available; do not change authentication.

Standalone `--issue` requires an embedding issue-content port. The ordinary
installed CLI does not provide a built-in GitHub reader. Read through `gh`,
investigate the repository, and prepare the typed local `--intent-file` described
in [the command protocol](tool-protocol.md).

Reuse an existing target-repository plan-only configuration, or prepare its path
and configuration records as part of the requested planning task. The current
standalone entry point requires a pinned `StandaloneRoot` even for planning;
`STANDALONE_ROOT_REQUIRED` means missing setup, not missing worker certification
or implementation permission. Keep the ceiling `plan`, use distinct repository,
store, worker and result paths, and leave worker runtime admission unavailable.
Where the root shape requires an admission selection, use an explicitly
non-admitted record; never fabricate successful conformance or copy a fixture's
accepted runtime/baseline. Worker dispatch must continue to reject that record.

The containing caller must still establish the existing process/resource limits
before CLR startup. Linux Git inspection also needs its supported ordinary
process scope. Skipping worker conformance does not bypass these restrictions.
An existing setup must be checked for the actual repository and current path/pin
identities before reuse.

### Freeze, attach, verify, then label

1. Obtain actual `COMPLETE / PLAN_FROZEN` output and retain its exact bundle,
   revision, contract identity and base commit. A readable draft or `INCOMPLETE`
   result is not a frozen plan. Do not rerun a successful freeze just to renew dates.
2. Prepare a self-contained issue comment containing the intent, findings versus
   hypotheses, exact base/revision, files and ordered work, acceptance/tests,
   prerequisites, rollout/rollback and remaining limitations. Include the frozen
   bundle when it is safe to publish. Exclude credentials, private host paths,
   operator configuration and private evidence. Do not redact or rewrite a frozen
   bundle and present it as retaining the original identity; if unsafe, publish a
   separate readable public handoff with the identity and keep private bytes local.
3. Post the complete comment through the authorized GitHub route, using a UTF-8
   body file with `gh`. Read it back and verify its complete body and issue identity.
   Reuse an exact existing comment. After an uncertain result, inspect remote state
   before another attempt; do not duplicate comments blindly.
4. Only after comment verification, create `planned` if absent, preserving existing
   label metadata, then add it to the issue without replacing other labels. Read
   back the final comment and labels. If posting or readback is incomplete, leave
   the label unapplied and report that the local plan exists but handoff is incomplete.
5. Report the plan-comment URL and verified label. A revised plan must identify
   which revision it supersedes; reconcile stale plan/label state before treating
   the issue as ready for downstream work.

A `planned` label is durable discoverability, not an execution lock or approval.
The Continuum execution lease is acquired on the actual implementation path before
worker provisioning/dispatch and released through that run's owned disposition.
Before implementation, reconcile the frozen base and obtain the separately
required execution, quality, review and runtime prerequisites.

## Platforms

Linux and Windows are v1 targets. Local evidence establishes Linux behavior; Windows requires its
hosted CI lane before merge. macOS is unclaimed.
