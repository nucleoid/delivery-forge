# Evidence component quality-gate reference

DeliveryForge.Evidence executes bounded commands, snapshots Git identity before and after a gate,
normalizes tool evidence, and writes canonical append-only gate-receipt contracts owned by
DeliveryForge.Contracts. It does not redefine the canonical schemas.

## Outcome rules

Adapters return exactly PASS, FAIL, INCOMPLETE, ERROR, or NOT_APPLICABLE.

- Valid tool pass/fail/inconclusive outcomes are preserved.
- Missing capability, coverage, tests, projects, compiler proof, complete mutation enumeration, or
  approved policy is INCOMPLETE, never PASS.
- Malformed, stale, mismatched, source-drifted, or otherwise orchestration-invalid evidence is ERROR.
- NOT_APPLICABLE requires a trusted non-empty rationale.
- Exit zero is evidence, not authority.

Every run records immutable argv, working directory, scope, protected policy/capability/configuration
identities, base/head/tree, timestamps, exit state, stdout/stderr hashes, source-drift status, outcome,
limitations, and rationale. A gate artifact directory and canonical receipt identity are append-only.

## Trust and baselines

Effective policy can come only from content-bound protected-Git-base or parent-admin authority.
Worker-branch and arbitrary caller-path policy is rejected. Fixture policy/baselines cannot grant
production authority. Baseline comparison is advisory until a parent-approved identity and explicit
changed-code allowance exist; omitted metrics or stale source revisions remain non-pass. Workers may
prepare proposals but cannot promote policy, baselines, thresholds, budgets, or exemptions.

## Capability matrix at this issue head

| Tool | Observed contract | Production acceptance |
| --- | --- | --- |
| .NET SDK | Pinned 10.0.401; real repository-owned RED/GREEN fixture proves failing and passing commits, cold compilation output, actual TRX counts, and a warm no-build case | Supported for the proven fixture scope; a warm/no-op run alone is incomplete compilation evidence |
| Crap4CSharp | Public result schema 1.x is parsed by version and tool identity; honest pass/fail/incomplete/error fixtures are marked fixture:true | Installed one-command acceptance is not yet certified; fixture results cannot authorize production |
| Mutate4CSharp | Current preview is recognized only as uncertified evidence | nucleoid/mutate4csharp#5 remains open; ENUMERATION_NOT_IMPLEMENTED and caller-asserted PASS remain INCOMPLETE |

Executable discovery is separate from OpenClaw skill discovery. The detector accepts a protected
explicit executable, PATH, or a repository-local .NET tool manifest contained by the repository root,
then runs bounded version and help probes and hashes the actual executable. It does not install,
restore, publish, or infer an unavailable capability.

## Rollout

Receipts are advisory first. Enforced no-regression gates require user-approved thresholds,
approvers, baseline identities, exemptions, and finite mutation budgets. Missing production policy
does not block parser/core tests, but it blocks a production PASS. No package publication,
deployment, service change, authentication change, migration, or production-wide mutation run is
performed by this component.
