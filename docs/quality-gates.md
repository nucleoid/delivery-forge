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

## Deterministic evaluation plugins

Gate execution resolves a trusted, code-registered deterministic evaluation plugin. The built-in .NET test, Crap4CSharp, and Mutate4CSharp adapters retain their existing default-enabled gate behavior behind compatibility wrappers. Trusted host code may register another implementation by stable ID/version/tool identity; frozen plans can select only registered IDs and cannot supply executable plugin code or commands.

Plugin configuration records an explicit enabled state. Disabling a selected required gate yields `INCOMPLETE`; it is evidence metadata, not a policy bypass. One code-owned gate/plugin registry drives protected-policy resolution, evidence writing, and consumer authority checks, including .NET, Crap4CSharp, Mutate4CSharp, and anti-slop mappings. An unmapped required gate remains unsatisfied and non-authoritative.

The bundled `dmmulroy.anti-slop` plugin derives JavaScript-family files from the exact frozen Git tree. Regular tracked files are mandatory unless their exact paths occur in the optional protected-base `.delivery-forge/anti-slop-ignore`; patch-head ignore changes have no authority. Paths, blob ids, Git modes, include/exclude decisions, and retired protected ignores contribute to the complete tracked-head identity. Protected ignores may exempt an explicitly named non-regular entry, and stale entries are retained as retired identity evidence rather than deadlocking a deletion or rename. Every selected working-tree file is rehashed with Git's blob algorithm and skip-worktree/assume-unchanged flags are rejected before linting. Blob reads use object ids and `cat-file`, never text conversion. Tree/diff/blob command captures are bounded to 64 MiB each and all scope reads plus execution share the gate's single finite deadline; exceeding either bound is `INCOMPLETE`. Any changed file containing a live ESLint/Oxlint disable/enable/line/next-line directive in either base or head is `INCOMPLETE`, so inherited or repositioned suppressions cannot widen coverage. Ordinary prose mentioning the tools is not a directive.

Its invocation is read-only and canonical. Small sets may use direct argv within a deterministic 24,000-byte budget. Larger sets use the code-owned Node batch host plus a hash-bound JSON input manifest; it uses argv arrays (never a shell), finite batches, bounded output, exact aggregate file/rule counts, and the same outer containment. Only the exact 19 generic rules run, with five Effect rules added by explicit opt-in. The configuration identity streams and binds the execution closure: manifests, batch host, input manifest, configs, vendored rules, lockfile, Oxlint JS/native/plugin, Node bytes/path, and environment. Fixture-only `tsx` and `typescript` development dependencies are not runtime authority. Syntax/parser/tool/start/runtime failures, missing runtime tooling, unsupported reports, and incomplete counts are `INCOMPLETE`; policy findings are `FAIL`. Zero applicable files never pass: protected policy may permit rationale-backed authoritative `NOT_APPLICABLE`, otherwise the gate is `INCOMPLETE`.

Implementation tracking: plugin contract and registry [#48](https://github.com/nucleoid/delivery-forge/issues/48), compatibility adapters [#49](https://github.com/nucleoid/delivery-forge/issues/49), pinned JavaScript/TypeScript anti-slop integration [#50](https://github.com/nucleoid/delivery-forge/issues/50), and tests/review/documentation [#51](https://github.com/nucleoid/delivery-forge/issues/51).

## Receipt contract

Every receipt records gate/policy IDs; base/head/tree; tool version and detected capability; config and baseline identity; exact command/invocation; scope; timestamps/exit state; outcome/reason; artifact paths/hashes; limitations/exclusions/missing data/N/A rationale; and whether source/tree changed during evaluation. Receipts are append-only evidence; summaries cannot erase failures or limitations.

Canonical gate receipt v1 is unchanged and remains readable as historical, non-authoritative evidence. Authoritative writers emit v2. Central authority validation covers both `PASS` and policy-permitted `NOT_APPLICABLE`, cross-checks binding fields against the structured command/config/input envelope and trusted pinned metadata, and rejects v1 N/A as authority. Receipt files must come from the trusted host-owned append-only store; SHA-256 identities prove integrity, not issuer provenance or a signature.

Rollout is readers-first: deploy this dual-version reader with `emitAuthoritativeV2: false` (also the constructor default), which keeps writing canonical v1 historical receipts; enable v2 explicitly only after every authority consumer is upgraded, then rerun required gates for in-flight work. While v1 writing is selected, authority decisions intentionally remain blocked. Rollback disables v2 writing but retains the dual-version reader until v2-producing runs expire or migrate; a v1-only binary is not a safe rollback target. Custom hosts must inject one code-owned registry consistently at policy resolution, gate execution, reference validation, workflow transitions, and publication.

The built-in `ci` authority is an explicit external-issuer boundary: Delivery Forge does not produce CI receipts. A trusted host must translate an authenticated GitHub Actions/Checks result for the exact head into tool version `github-actions-v1` and format `github-checks-v1`, then append the receipt to a host-owned append-only store. Repository files and workflow-produced patch artifacts are inputs only and cannot establish authoritative receipt provenance.

## Baseline and policy ownership

Roll out from advisory reporting to reviewed baseline/exemption registries, then enforce no unapproved changed-code regression. Workers cannot weaken thresholds, omit required projects, broaden exemptions, regenerate a favorable baseline, or self-approve policy changes. Mutation survivors prompt stronger behavior tests, not suppression.

## Exact-patch review

Independent review binds to base/head/tree and exact patch hash. Edits, amend, rebase, conflict resolution, generated changes, or dependency integration trigger invalidation assessment. Material patch changes require fresh review.

## Failure scenarios kept visible

Stale plan/changed base; conflicting writer/uncertain lease; interrupted command; incomplete mutation enumeration; missing/stale coverage; baseline/exemption gaming; review of another patch/head; green partial tests without required full gates; and installed tools lacking required capability.
