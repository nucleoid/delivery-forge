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

The bundled `dmmulroy.anti-slop` plugin derives JavaScript-family files from the exact frozen Git tree. Regular tracked files are mandatory unless their exact paths occur in the optional protected-base `.delivery-forge/anti-slop-ignore`; patch-head ignore changes have no authority. Paths, blob ids, Git modes, include/exclude decisions, and retired protected ignores contribute to the complete tracked-head identity. Protected ignores may exempt an explicitly named non-regular entry, and stale entries are retained as retired identity evidence rather than deadlocking a deletion or rename. Selected blobs are obtained through bounded `git cat-file --batch-check` and `git cat-file --batch` groups, verified in memory with Git's object hash, and written byte-for-byte to a fresh private host-owned root outside the repository. They are made read-only, linted there using repository-relative paths, and removed after receipt persistence. This preserves the committed bytes despite checkout filters, `autocrlf`, smudge/LFS behavior, or index hints such as skip-worktree and assume-unchanged; repository cleanliness is bound separately. The receipt records the frozen object/input-set identity and a deterministic logical materialization label, not the random private path. Object/file/aggregate counts and sizes are bounded, paths are consumed from NUL-delimited Git output, and all phases consume one finite gate deadline; a missing/malformed object, ambiguity, cancellation, or exceeded bound is `INCOMPLETE`.

Protected ignored paths are not suppression-scanned. One production lexer/parser performs a linear, byte-bounded pass over base and head JavaScript-family blobs. It recognizes complete line and block comment bodies under pinned Oxlint directive semantics without raw-scanning strings, templates, regular expressions, or prose. It normalizes protected-rule and blanket coverage into semantic multisets: an inherited directive, or a narrower head directive already covered by base, is legitimate; a newly introduced or widened blanket, `anti-slop`, `anti-slop-effect`, or `oxc/no-accumulating-spread` coverage is `INCOMPLETE`. Directive counts, body length, rule count, and rule length are capped, with shared byte-interval cancellation/deadline checks. Ambiguous or over-cap input fails closed as `INCOMPLETE`.

Its invocation is read-only and canonical. Small sets may use direct argv within a deterministic 24,000-byte budget. Larger sets use the code-owned Node batch host plus a hash-bound JSON input manifest; it uses argv arrays (never a shell), finite batches, bounded output, exact aggregate file/rule counts, and the same outer containment. Manifest/path/batch/diagnostic sizes are capped. Every Oxlint child receives the shrinking remainder of one shared deadline, and .NET owns the Node/Oxlint process tree so timeout or cancellation kills the whole tree. Only the exact 19 generic rules run, with five Effect rules added by explicit opt-in. The configuration identity streams and binds the execution closure: manifests, batch host, input manifest, configs, vendored rules, lockfile, Oxlint JS/native/plugin, Node bytes/path, and environment. Fixture-only `tsx` and `typescript` development dependencies are not runtime authority. Syntax/parser/tool/start/runtime failures, missing runtime tooling, unsupported reports, incomplete counts, and cap/deadline failures are `INCOMPLETE`; policy findings are `FAIL`. Zero applicable files never pass: protected policy may permit rationale-backed authoritative `NOT_APPLICABLE`, otherwise the gate is `INCOMPLETE`. Authoritative anti-slop N/A passes the same exact Oxlint `1.78.0`, plugin `0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7`, trusted-bundle/config-closure, registry/capability/input/provenance, and production-execution validation as an executed result.

Implementation tracking: plugin contract and registry [#48](https://github.com/nucleoid/delivery-forge/issues/48), compatibility adapters [#49](https://github.com/nucleoid/delivery-forge/issues/49), pinned JavaScript/TypeScript anti-slop integration [#50](https://github.com/nucleoid/delivery-forge/issues/50), and tests/review/documentation [#51](https://github.com/nucleoid/delivery-forge/issues/51).

## Receipt contract

Every receipt records gate/policy IDs; base/head/tree; tool version and detected capability; config and baseline identity; exact command/invocation; scope; timestamps/exit state; outcome/reason; artifact paths/hashes; limitations/exclusions/missing data/N/A rationale; and whether source/tree changed during evaluation. Receipts are append-only evidence; summaries cannot erase failures or limitations.

Canonical gate receipt v1 is unchanged and remains readable as historical, non-authoritative evidence. Authoritative writers emit v2. Central authority validation covers both `PASS` and policy-permitted `NOT_APPLICABLE`, cross-checks binding fields against the structured command/config/input envelope and trusted pinned metadata, and rejects v1 N/A as authority. `AuthoritativeEvaluationHost` is the code-owned production composition: it always emits v2 through `HostOwnedReceiptStore`. The store creates immutable receipt bytes plus an identity-indexed provenance ledger entry under a private, non-reparse root, flushes both durably, and revalidates canonical bytes and the ledger hash on every read. A fresh process can therefore verify issuer provenance without an in-memory capability; a copied or modified repository receipt is not authoritative.

Mixed-version rollout is producers before enforcing consumers. First provision the private store and deploy `AuthoritativeEvaluationHost` producers while retaining dual-version parsing; validate that v2 receipt bytes and ledger entries survive a process restart. Only then enable workflow/publication consumers to require v2 authority. Canonical v1 remains readable throughout but never satisfies authority, so in-flight v1-only runs must rerun required gates. Consumer activation must not precede a working v2 producer and durable ledger.

For rollback, first disable authority-dependent consumer transitions, retain v2-capable readers and durable provenance for all live receipts, then stop v2 producers after work drains. Never roll a reader back to a v1-only binary while v2 receipts remain live, and never promote v1 as a fallback authority. Gate claims are durable files containing the owner PID plus Linux boot ID and process-start identity (or the corresponding exact process identity on other supported hosts). A live owner is never stolen. A stale owner is atomically preserved as interrupted evidence; that invocation emits durable `INCOMPLETE`, and a later invocation may retry into a fresh immutable `.retry-*` directory. Oversized or ambiguous claims also fail closed to durable `INCOMPLETE`.

The built-in `ci` authority is an explicit external-issuer boundary: Delivery Forge does not produce CI receipts. A trusted host must translate an authenticated GitHub Actions/Checks result for the exact head into tool version `github-actions-v1` and format `github-checks-v1`, then append the receipt to a host-owned append-only store. Repository files and workflow-produced patch artifacts are inputs only and cannot establish authoritative receipt provenance.

## Baseline and policy ownership

Roll out from advisory reporting to reviewed baseline/exemption registries, then enforce no unapproved changed-code regression. Workers cannot weaken thresholds, omit required projects, broaden exemptions, regenerate a favorable baseline, or self-approve policy changes. Mutation survivors prompt stronger behavior tests, not suppression.

## Exact-patch review

Independent review binds to base/head/tree and exact patch hash. Edits, amend, rebase, conflict resolution, generated changes, or dependency integration trigger invalidation assessment. Material patch changes require fresh review.

## Failure scenarios kept visible

Stale plan/changed base; conflicting writer/uncertain lease; interrupted command; incomplete mutation enumeration; missing/stale coverage; baseline/exemption gaming; review of another patch/head; green partial tests without required full gates; and installed tools lacking required capability.
