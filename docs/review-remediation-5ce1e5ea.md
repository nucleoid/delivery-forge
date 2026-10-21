# Final review remediation — 5ce1e5ea

## Disposition

1. Template-expression JSX now fails closed before quote classification. The exact reviewed TSX input has a scanner regression and matched real Oxlint 1.78.0 suppressed/unsuppressed controls.
2. Pre-override plugin results are named `pluginRawOutcome`. Canonical v1 receipts omit plugin metadata; future-v2 metadata uses `pluginRawOutcome`, never `toolOutcome`.
3. Legacy-v1 authority is restored only for the default code-owned built-in registry and only where the protected-base checks already permit it: protected policy and required-gate binding, registry-pinned plugin/tool/version/format, non-fixture capability, and production-capable normalized evidence. A real immutable .NET 10.0.401 integration proves complete clean test evidence produces canonical-v1 `PASS`; fixture, caller-injected/custom registry, and anti-slop success remain advisory.
4. Canonical v1 command-envelope shape and public emission are preserved. Existing v1 consumer acceptance is unchanged; future-v2 production and consumer activation remain disabled pending a genuinely protected producer.
5. One trusted registry carries gate/plugin metadata and the legacy-v1 authority marker; there is no parallel gate allow-list.

## Pins and boundary

- anti-slop upstream `13909c9e07590f5a85d93c62712259fe14a883f7`, MIT
- private unpublished plugin `0.1.2`
- `oxlint` and `@oxlint/plugins` `1.78.0`
- fixture-only `tsx` `4.23.12` and TypeScript `7.0.2`
- Effect rules remain explicit opt-in

The same-UID host still cannot issue future-v2 authority. `AuthoritativeEvaluationHost` remains unavailable and production v2 consumers remain disabled. This limitation does not retroactively redefine canonical v1 producer or consumer behavior.

## Validation

Every SDK, Node, Oxlint, build, test, and format command used the outer `/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock`, a finite `/usr/bin/timeout`, and a user `systemd-run --scope` with `MemoryMax=1024M`, `MemorySwapMax=128M`, and `CPUQuota=100%`. The immutable SDK was `10.0.401`; build servers, node reuse, shared compilation, and parallel build were disabled.

- focused exact-review/receipt/override/real-.NET regressions: **4/4 passed**
- pinned Oxlint fixture integration: clean PASS, violation FAIL, malformed INCOMPLETE, eleven existing suppression attacks, matched template-regex controls, and the exact matched template-JSX suppressed/unsuppressed controls passed
- final Release solution build: **0 warnings, 0 errors**
- crash driver and all three native fixture helpers: **0 warnings, 0 errors**
- final unfiltered Release solution: **1,088/1,088 passed**
- `dotnet format DeliveryForge.slnx --no-restore --verify-no-changes --verbosity minimal`: exit 0
- `git diff --check`: clean

One intermediate focused run exposed that the real .NET fixture still used an advisory policy and was corrected to exercise the protected policy it claimed. One intermediate full run then exposed a stale unit assertion expecting complete .NET evidence to remain non-production-capable; after correcting that expectation, the final unfiltered run above passed in one invocation.
