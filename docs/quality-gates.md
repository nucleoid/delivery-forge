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

Plugin configuration records an explicit enabled state. Disabling a selected required gate yields `INCOMPLETE`; it is evidence metadata, not a policy bypass. Receipts bind the effective plugin ID/version, enabled/status metadata, configuration identity, normalized input list and input-set hash inside the existing v1 `command` envelope, preserving the canonical gate-receipt schema.

The bundled `dmmulroy.anti-slop` plugin automatically derives repository-relative `.js`, `.jsx`, `.ts`, `.tsx`, `.mjs`, `.cjs`, `.mts`, and `.cts` files from the exact frozen Git tree. It applies frozen repository/tool ignores itself and requires caller metadata to match the complete derived set. Partial caller scope cannot pass.

Its invocation is read-only and canonical: a code-owned registered bundle, absolute Node runtime, package-local Oxlint launcher, cleared allow-listed environment, JSON output, and exact selected inputs. The configuration identity binds the package manifests, selected/imported config, vendored rules, lockfile, Oxlint JavaScript runtime, plugin package, installed native binding, Node bytes/path, and environment. The closure is revalidated after execution. Autofix and option-like input paths are refused. Syntax/parser/tool/runtime failures, missing installs or filenames, unsupported report shapes, zero configured rules, and incomplete file counts are `INCOMPLETE`; policy findings are `FAIL`. A clean anti-slop result is advisory unless the protected policy resolver has an exact trusted gate-to-plugin ID/version/tool binding for that gate. Effect rules require explicit opt-in. Vendoring, immutable upstream revision, licenses, exact tool pins, and real fixture verification are recorded in `components/evidence/anti-slop/PROVENANCE.md`.

Implementation tracking: plugin contract and registry [#48](https://github.com/nucleoid/delivery-forge/issues/48), compatibility adapters [#49](https://github.com/nucleoid/delivery-forge/issues/49), pinned JavaScript/TypeScript anti-slop integration [#50](https://github.com/nucleoid/delivery-forge/issues/50), and tests/review/documentation [#51](https://github.com/nucleoid/delivery-forge/issues/51).

## Receipt contract

Every receipt records gate/policy IDs; base/head/tree; tool version and detected capability; config and baseline identity; exact command/invocation; scope; timestamps/exit state; outcome/reason; artifact paths/hashes; limitations/exclusions/missing data/N/A rationale; and whether source/tree changed during evaluation. Receipts are append-only evidence; summaries cannot erase failures or limitations.

## Baseline and policy ownership

Roll out from advisory reporting to reviewed baseline/exemption registries, then enforce no unapproved changed-code regression. Workers cannot weaken thresholds, omit required projects, broaden exemptions, regenerate a favorable baseline, or self-approve policy changes. Mutation survivors prompt stronger behavior tests, not suppression.

## Exact-patch review

Independent review binds to base/head/tree and exact patch hash. Edits, amend, rebase, conflict resolution, generated changes, or dependency integration trigger invalidation assessment. Material patch changes require fresh review.

## Failure scenarios kept visible

Stale plan/changed base; conflicting writer/uncertain lease; interrupted command; incomplete mutation enumeration; missing/stale coverage; baseline/exemption gaming; review of another patch/head; green partial tests without required full gates; and installed tools lacking required capability.
