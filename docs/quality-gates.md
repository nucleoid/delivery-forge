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

The bundled `dmmulroy.anti-slop` plugin derives JavaScript-family files from the exact frozen Git tree. Regular tracked files are mandatory unless their exact paths occur in the optional protected-base `.delivery-forge/anti-slop-ignore`; patch-head ignore changes have no authority. Paths, blob ids, Git modes, include/exclude decisions, and retired protected ignores contribute to the complete tracked-head identity. Protected ignores may exempt an explicitly named non-regular entry, and stale entries are retained as retired identity evidence rather than deadlocking a deletion or rename. Selected blobs are materialized by object id into a fresh private host-owned root outside the repository, re-hashed with Git's object algorithm, made read-only, linted there using repository-relative paths, and removed after receipt persistence. The receipt records the frozen object/input-set identity and a deterministic logical materialization label, not the random private path. Skip-worktree/assume-unchanged flags and reparse inputs are rejected. Tree/diff/blob command captures are bounded to 64 MiB, changed suppression scans are linear and capped at 4 MiB per file with periodic cancellation/deadline checks, and all phases consume one finite gate deadline; exceeding a bound is `INCOMPLETE`.

Protected ignored paths are not suppression-scanned. In changed, non-ignored files, narrow directives for unrelated rules are legitimate. Blanket disable directives, anti-slop or `oxc/no-accumulating-spread` directives, and ambiguous directive-like spellings can never coexist with authoritative PASS. Detection deliberately searches conservatively across all bytes rather than guessing JavaScript regex/string grammar: a marker inside an ambiguous literal or malformed construct yields `INCOMPLETE`, closing parser-heuristic bypasses at the cost of fail-closed false positives. Base and head are both bounded/scanned so removed, moved, and retained coverage is explicit.

Its invocation is read-only and canonical. Small sets may use direct argv within a deterministic 24,000-byte budget. Larger sets use the code-owned Node batch host plus a hash-bound JSON input manifest; it uses argv arrays (never a shell), finite batches, bounded output, exact aggregate file/rule counts, and the same outer containment. Only the exact 19 generic rules run, with five Effect rules added by explicit opt-in. The configuration identity streams and binds the execution closure: manifests, batch host, input manifest, configs, vendored rules, lockfile, Oxlint JS/native/plugin, Node bytes/path, and environment. Fixture-only `tsx` and `typescript` development dependencies are not runtime authority. Syntax/parser/tool/start/runtime failures, missing runtime tooling, unsupported reports, and incomplete counts are `INCOMPLETE`; policy findings are `FAIL`. Zero applicable files never pass: protected policy may permit rationale-backed authoritative `NOT_APPLICABLE`, otherwise the gate is `INCOMPLETE`.

Implementation tracking: plugin contract and registry [#48](https://github.com/nucleoid/delivery-forge/issues/48), compatibility adapters [#49](https://github.com/nucleoid/delivery-forge/issues/49), pinned JavaScript/TypeScript anti-slop integration [#50](https://github.com/nucleoid/delivery-forge/issues/50), and tests/review/documentation [#51](https://github.com/nucleoid/delivery-forge/issues/51).

## Receipt contract

Every receipt records gate/policy IDs; base/head/tree; tool version and detected capability; config and baseline identity; exact command/invocation; scope; timestamps/exit state; outcome/reason; artifact paths/hashes; limitations/exclusions/missing data/N/A rationale; and whether source/tree changed during evaluation. Receipts are append-only evidence; summaries cannot erase failures or limitations.

Canonical gate receipt v1 is unchanged and remains readable as historical, non-authoritative evidence. Authoritative writers emit v2. Central authority validation covers both `PASS` and policy-permitted `NOT_APPLICABLE`, cross-checks binding fields against the structured command/config/input envelope and trusted pinned metadata, and rejects v1 N/A as authority. Receipt files must come from the trusted host-owned append-only store; SHA-256 identities prove integrity, not issuer provenance or a signature.

Rollout is readers-first: deploy the dual-version reader with `emitAuthoritativeV2: false` (the constructor default), preserving unchanged canonical v1 schema/readability while v1 remains historical and non-authoritative. Upgrade every authority consumer and provision an absolute, private, non-reparse `HostOwnedReceiptStore`; then flip the trusted host composition to `emitAuthoritativeV2: true`. The runner refuses that flag without the host store, appends validated canonical v2 bytes through it, and returns the opaque provenance capability alongside the result for workflow/publication consumers. The in-tree trusted middle-driver composition demonstrates that wiring. Rerun required gates for in-flight v1 runs after the flip.

Rollback is mixed-version, not wishful thinking: first stop new v2 runs, retain v2-capable readers and their host provenance until existing v2 runs expire or migrate, then disable v2 writing. Never roll a reader back to a v1-only binary while v2 receipts remain live. A v1 write does not satisfy authority during either direction of the transition. Completed interrupted/INCOMPLETE attempts may retry under the same gate id into a fresh immutable `.retry-*` artifact directory; a live delete-on-close claim excludes concurrent execution and is released on every returned outcome.

The built-in `ci` authority is an explicit external-issuer boundary: Delivery Forge does not produce CI receipts. A trusted host must translate an authenticated GitHub Actions/Checks result for the exact head into tool version `github-actions-v1` and format `github-checks-v1`, then append the receipt to a host-owned append-only store. Repository files and workflow-produced patch artifacts are inputs only and cannot establish authoritative receipt provenance.

## Baseline and policy ownership

Roll out from advisory reporting to reviewed baseline/exemption registries, then enforce no unapproved changed-code regression. Workers cannot weaken thresholds, omit required projects, broaden exemptions, regenerate a favorable baseline, or self-approve policy changes. Mutation survivors prompt stronger behavior tests, not suppression.

## Exact-patch review

Independent review binds to base/head/tree and exact patch hash. Edits, amend, rebase, conflict resolution, generated changes, or dependency integration trigger invalidation assessment. Material patch changes require fresh review.

## Failure scenarios kept visible

Stale plan/changed base; conflicting writer/uncertain lease; interrupted command; incomplete mutation enumeration; missing/stale coverage; baseline/exemption gaming; review of another patch/head; green partial tests without required full gates; and installed tools lacking required capability.
