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

Every run records immutable argv, working directory, scope, protected policy, detected capability,
configuration identity, Git-verified base/head/tree, timestamps, exit state, stdout/stderr hashes,
source-drift status, outcome, limitations, and rationale. GateRunner selects a sealed built-in adapter;
callers cannot supply a normalizer. A gate claim and canonical receipt are append-only.

## Trust and baselines

Effective policy can come only from content-bound protected-Git-base or parent-admin authority.
Worker-branch and arbitrary caller-path policy is rejected. Fixture policy/baselines cannot grant
production authority. Baseline comparison is advisory until a parent-approved identity and explicit
changed-code allowance exist; omitted metrics or stale source revisions remain non-pass. Workers may
prepare proposals but cannot promote policy, baselines, thresholds, budgets, or exemptions.
Protected policy resolution verifies the exact commit, ancestry, protected Git bytes, canonical schema,
and contract identity. GateRunner additionally requires the policy revision/root to equal its
parent-frozen repository base/root and records policy provenance in the receipt. The proposal-only
resolver remains non-authoritative. No parent-approved baseline promotion source exists yet, so
no-regression results remain advisory.

## Capability matrix at this issue head

| Tool | Observed contract | Production acceptance |
| --- | --- | --- |
| .NET SDK | Pinned 10.0.401; the detector probes from the gate working root; GateRunner derives counts from the bound TRX, SDK version from detector-issued capability, compiler evidence from bound invocation/output, and rejects warm `--no-build`; the real fixture uses real Git identities through runner → producer → adapter for RED/GREEN/warm receipts | Advisory until an approved policy binds the complete required project/coverage set; no production PASS is claimed |
| Crap4CSharp | Public result schema 1.x is parsed by version and tool identity; honest pass/fail/incomplete/error fixtures are marked fixture:true | Installed one-command acceptance is not yet certified; fixture results cannot authorize production |
| Mutate4CSharp | Current preview is recognized only as uncertified evidence | nucleoid/mutate4csharp#5 remains open; ENUMERATION_NOT_IMPLEMENTED and caller-asserted PASS remain INCOMPLETE |

Executable discovery is separate from OpenClaw skill discovery. The detector accepts a protected
explicit executable, PATH, or a repository-local .NET tool manifest contained by the repository root,
hashes the resolved host before and after bounded version/help probes, and rejects swaps. A manifest
route is unsupported until it can bind and hash the exact entry package as well as the dotnet host.
GateRunner accepts only detector-issued capabilities, executes only the exact detected path, rechecks
its hash afterward, keeps artifacts outside the frozen worktree, and requires owned-process quiescence
after interruption. The detector does not install, restore, publish, or infer an unavailable capability.

## Rollout

Receipts are advisory first. Enforced no-regression gates require user-approved thresholds,
approvers, baseline identities, exemptions, and finite mutation budgets. Missing production policy
does not block parser/core tests, but it blocks a production PASS. No package publication,
deployment, service change, authentication change, migration, or production-wide mutation run is
performed by this component.
