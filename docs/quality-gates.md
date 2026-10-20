# Quality gates and evidence receipts

## Layered evaluation

1. **Meaningful RED/GREEN** — show the intended behavior fails for the right reason, then passes after implementation.
2. **Affected tests** — cover changed behavior, callers, contracts, and known impact surfaces.
3. **Required full/build gates** — satisfy repository policy with clean builds/full suites where required.
4. **Complexity and coverage debt** — measure changed-code risk and prevent unapproved threshold/baseline regression.
5. **Mutation strength** — use survivors to drive real behavior tests; enumeration must be complete enough for the claimed scope.
6. **Independent cross-family review** — review the exact patch/head with a genuinely independent model/tool family where policy requires it.

Scope is risk-proportionate. Documentation-only work may mark code/test/mutation gates `NOT_APPLICABLE`, but each N/A needs a concrete reason.

## Gate outcomes

Every gate returns exactly one outcome: `PASS`, `FAIL`, `INCOMPLETE`, `ERROR`, or `NOT_APPLICABLE`. Timeout, unsupported capability, missing coverage/artifacts, truncated results, or incomplete mutation enumeration never map to `PASS`.

## Receipt contract

Every receipt records gate/policy IDs; base/head/tree; tool version and detected capability; config and baseline identity; exact command/invocation; scope; timestamps/exit state; outcome/reason; artifact paths/hashes; limitations/exclusions/missing data/N/A rationale; and whether source/tree changed during evaluation. Receipts are append-only evidence; summaries cannot erase failures or limitations.

## Baseline and policy ownership

Roll out from advisory reporting to reviewed baseline/exemption registries, then enforce no unapproved changed-code regression. Workers cannot weaken thresholds, omit required projects, broaden exemptions, regenerate a favorable baseline, or self-approve policy changes. Mutation survivors prompt stronger behavior tests, not suppression.

## Exact-patch review

Independent review binds to base/head/tree and exact patch hash. Edits, amend, rebase, conflict resolution, generated changes, or dependency integration trigger invalidation assessment. Material patch changes require fresh review.

## Failure scenarios kept visible

Stale plan/changed base; conflicting writer/uncertain lease; interrupted command; incomplete mutation enumeration; missing/stale coverage; baseline/exemption gaming; review of another patch/head; green partial tests without required full gates; and installed tools lacking required capability.

## Completed official-tool providers

`InstalledToolGateProvider` consumes a separately parent-pinned `InstalledToolProfile`; it does not install packages or infer trust from a report's version string. The profile distinguishes package version, observed informational version, source commit, complete installed payload, selected host/SDK/Git, configuration and effective environment. Actual version/help, original process result, complete report, frozen base/head/tree, source/index/status, package bytes and artifact bundle must join. Missing, unsupported, truncated or mismatched evidence is nonpass. The report-only adapters remain advisory.

Installed Mutate profiles use their packaged default graph deployment. Both `MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST` and `MUTATE4CSHARP_GRAPH_CAPTURE_MANIFEST_SHA256` must be absent from the profile and inherited host environment, including case aliases. The actual executor repeats this check inside admission immediately before each native birth, including per-invocation overrides. Empty override entries are refused too; a parent-pinned profile or fixture flag cannot select external helper code outside the installed payload census.

Mutation uses the supported complete `check --base <frozen> --report <fresh-owned-file> --no-state` path. It retains all configured suites and complete accounting; preview enumeration, cache/target sampling and unproven state reuse cannot pass. Its explicit configuration must remain within one worker,100 mutants and600 seconds, also within the enclosing original budget.

Crap uses the supported fresh `check` path and exports the complete artifact bundle. Historical replay is not current evidence. Raw configuration hash and upstream canonical trusted-policy hash are separate: both identities and complete policy semantics are checked. The required tests/coverage/crap set and CRAP ceiling30 cannot be weakened. Approved baseline/exemption paths and identities remain protected policy inputs; this provider neither creates nor approves them. Existing reviewed-baseline no-regression comparison remains a separate required quality subgate.

Changed-LINE coverage uses the same actual check's captured source bytes and stock OpenCover line projection, plus a complete pre-main frozen-base Git zero-context C# diff. It counts distinct changed executable lines, combines repeated runs within the same context, and requires hits across observed contexts. At least80% is required. This is separate from callable/sequence-point coverage and CRAP. Unmapped source, missing context/report, unsupported coordinates, malformed/truncated hunks and an empty measurable denominator remain nonpass; no exemption/N/A is manufactured.

The provider's original whole limit is at most1200 seconds, work at most1100 and cleanup the same earliest once60 bounded by the whole. Optional shared process budgets only restrict existing native-owner grants. Cleanup imports are synchronous at the actual owner transition; exported QPC/frequency fields are DATA, not adoption authority. No new SDK work starts after main cleanup. Actual caller2CPU affinity and at-most4GiB managed limit are checked before use; tool/CLR startup before the containing admission is not retroactively certified. No persistent build servers are admitted. Synchronous OS/runtime work remains cooperatively bounded, with late/unknown resources retained.

Current private installed-tool execution presently requires the existing Windows creation-owned Job path. Non-Windows provider calls refuse before any tool/Git/SDK invocation: a naturally exited root and closed redirected pipes cannot establish descendant settlement. This temporary safety refusal does not complete Linux support. A genuine shared creation-owned Linux scope, resource admission, bound process/cgroup identity, kernel-empty closure and real descendant/root-exit/closed-pipe/timeout/failure regressions remain mandatory; a caller profile or report cannot supply that authority. Legacy ordinary capture defaults are unchanged and are not promoted as supported Linux current evidence.

All newly added provider, true-line and budget tests are currently **UNEXECUTED**. Actual installed-package/probe/current-run evidence, genuine RED/GREEN, affected/full gates, owner-approved baselines/exemptions and independent review remain mandatory before complete Forge quality acceptance. Fixture receipts cannot satisfy real producer or production authority. Continuum installation/integration remains separate.
