# Final-review remediation (`d877cad6`)

Protected base: `d34280b7f578acca04299009ca6e2bd53e6f0427`

Reviewed parent: `d877cad6f1ec35bcf6683bb88c9e968ad57ffe55`

## Finding dispositions

1. The JavaScript suppression scanner now fails closed when `/` after `}` is ambiguous, recognizes `await` and `yield` expression prefixes, and rejects JSX expression-container ambiguity. Exact scanner regressions and matched real Oxlint suppressed/unsuppressed fixtures cover block-statement regex, `await` regex, and JSX-container cases while retaining division/property controls.
2. Production transition, manifest-reference, and publication consumers always validate v2 authority-binding semantics. Advisory, disabled, or non-production-capable v2 `PASS`/`NOT_APPLICABLE` cannot satisfy a required gate while v2 provenance enforcement is disabled. Canonical-v1 and mixed v1/v2 behavior remain compatible.
3. `CreateBuiltIns(...)` carries a factory-only code-owned registry marker, so its exact sealed built-in .NET adapters retain legacy-v1 authority. Publicly constructed, rewrapped, custom, or lookalike registries do not inherit it; anti-slop remains advisory.
4. Oxlint exits outside `0/1` normalize to `INCOMPLETE` before findings can classify the result as `FAIL`.

Canonical-v1 command-envelope bytes, protected gate names/bindings, receipt ordering, and pinned runtime/provenance closure were exercised by the focused and full suites. `ERROR`, disabled-gate, and zero-file behavior are unchanged.

## Pins and licensing

- anti-slop upstream `13909c9e07590f5a85d93c62712259fe14a883f7`, MIT
- plugin `0.1.2`
- `oxlint` and `@oxlint/plugins` `1.78.0`
- fixture-only `tsx` `4.23.12` and TypeScript `7.0.2`
- Effect rules remain opt-in; no config rewrite or autofix was introduced

## Validation

Every SDK, Node, Oxlint, fixture, build, test, and format invocation used the outer `/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock`, a finite timeout, and a user `systemd-run --scope --collect --quiet` with `MemoryMax=1024M`, `MemorySwapMax=128M`, and `CPUQuota=100%`. SDK commands used retained .NET SDK `10.0.401`, no restore, and disabled build servers, node reuse, shared compilation, and parallel build.

- Focused RED: 5/5 evidence and 2/2 contract regressions failed for the intended reasons.
- Focused GREEN: 17/17 evidence/security and 3/3 authority-consumer/mixed-version cases passed.
- Real pinned Oxlint fixture suite passed, including matched exact reviewer attacks and unsuppressed controls.
- Production batch host: exit 0, one file, 19 rules, no diagnostics.
- Release solution build: 0 warnings, 0 errors.
- Full unfiltered Release suite: 1,099/1,099 passed (Contracts 148, Coordination 112, End-to-End 118, Evidence 259, Execution 88, Planning 104, Publication 120, Review 58, Scheduling 92).
- `dotnet format DeliveryForge.slnx --no-restore --verify-no-changes`: passed.
- `git diff --check`: passed.

One Oxlint preflight using `NODE_OPTIONS=--no-addons` was discarded because it disabled the pinned native addon and emitted no report. The canonical package-local command and production batch host then passed. Windows-native behavior remains compile-only on this Linux host, and future-v2 production authority remains disabled pending a genuinely isolated producer and provenance path.
