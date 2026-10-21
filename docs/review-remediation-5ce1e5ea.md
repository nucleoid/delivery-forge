# Final review remediation — 5ce1e5ea

## Disposition

1. Template-expression JSX now fails closed before quote classification. The exact reviewed TSX input has a scanner regression and matched real Oxlint 1.78.0 suppressed/unsuppressed controls.
2. Pre-override plugin results are named `pluginRawOutcome`. Canonical v1 receipts omit plugin metadata; future-v2 metadata uses `pluginRawOutcome`, never `toolOutcome`.
3. Legacy-v1 authority is restored only for the default code-owned built-in registry and only where the protected-base checks already permit it: protected policy and required-gate binding, registry-pinned plugin/tool/version/format, non-fixture capability, and production-capable normalized evidence. A real immutable .NET 10.0.401 integration proves complete clean test evidence produces canonical-v1 `PASS`; fixture, caller-injected/custom registry, and anti-slop success remain advisory.
4. Canonical v1 command-envelope shape and public emission are preserved. A follow-up protected-base comparison found that `ContractReferenceValidator` had nevertheless made future-v2 binding and provenance mandatory for every run manifest. That regression is corrected below; future-v2 production and production consumer activation remain disabled pending a genuinely protected producer.
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

## Follow-up contract-reference compatibility correction

The audit against protected base `d34280b7f578acca04299009ca6e2bd53e6f0427` was grounded. The protected-base public `ContractReferenceValidator.Validate` accepted canonical-v1 `PASS` from the exact immutable policy/base/head/tree references. The reviewed implementation instead unconditionally called the future-v2 `GateReceiptAuthority` validator and required host-owned provenance, contradicting the documented rollout boundary.

The public validator now uses the same immutable `GateReceiptConsumerPolicy.Disabled` boundary as workflow transition and publication preflight. It accepts canonical-v1 `PASS`, policy-allowed canonical-v1 `NOT_APPLICABLE`, and mixed v1/v2 manifests under the existing schema, policy, required-gate, base, head, tree, plan, and replay-reference checks. This restores compatibility only; it does not apply v2 authority to v1 receipts or broaden legacy producer trust.

Future-v2 enforcement is available only through the isolated internal validation path. That path consumes `GateReceiptConsumerPolicy`, checks its pinned registry identity against the single `TrustedGatePluginRegistry`, validates the v2 authority binding and command envelope through `GateReceiptAuthority`, and requires host-owned append-only provenance. The public method has no registry, provenance, boolean, or registry-identity activation parameters. Production remains fixed to `GateReceiptConsumerPolicy.Disabled`.

### Follow-up validation

Every SDK command used the outer `/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock`, a finite `/usr/bin/timeout`, and user `systemd-run --scope --collect --quiet` with `MemoryMax=1024M`, `MemorySwapMax=128M`, and `CPUQuota=100%`. The immutable SDK was `10.0.401`; restore was disabled, as were build servers, node reuse, shared compilation, and parallel build. No Node, npm, Oxlint, restore, or install command was needed for this contract-only correction.

- Focused RED (`dotnet test components/contracts/DeliveryForge.Contracts.Tests/DeliveryForge.Contracts.Tests.csproj --configuration Release --no-restore --no-build ... --filter "FullyQualifiedName~Default_validator_preserves_canonical_v1_manifest_and_replay_acceptance|FullyQualifiedName~Default_validator_accepts_mixed_v1_and_v2_manifest_and_replay_evidence"`): **0 passed, 3 failed**, each with `Gate outcome lacks a canonical authority binding`; exit 1.
- Final focused GREEN (the same compatibility cases plus explicit future-v2 authority and public activation-surface regressions): **7 passed, 0 failed**; exit 0.
- Release solution build: **0 warnings, 0 errors**; exit 0. The crash driver and both out-of-solution native helper builds also completed with **0 warnings, 0 errors**.
- Corrected unfiltered Release solution test: **1,092 passed, 0 failed**; exit 0 (Contracts 146, Coordination 112, End-to-End 118, Evidence 254, Execution 88, Planning 104, Publication 120, Review 58, Scheduling 92).
- `dotnet format DeliveryForge.slnx --no-restore --verify-no-changes --verbosity minimal`: exit 0.

The first unfiltered attempt was discarded as validation configuration, not product evidence: `DELIVERY_FORGE_ACTUAL_CRASH_DRIVER_DLL` incorrectly named the Linux apphost even though the fixture launches it as `dotnet <driver>`. Eight crash-driver cases exited 1 before their ready marker while every other project passed. A bounded lane diagnostic reproduced the exact routing error. Supplying the built crash-driver `.dll` and retaining apphosts for the native helper variables produced the final 1,092/1,092 unfiltered result above.
