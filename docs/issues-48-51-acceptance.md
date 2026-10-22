# Deterministic evaluation issues 48–51 acceptance

This report maps the implemented, tested surface to issues #48–#51. It does not claim production deployment, automatic delivery-pipeline invocation, installed-tool certification for external .NET analyzers, or future-v2 authority.

## #48 — plugin contract and registry

- `IDeterministicEvaluationPlugin`, versioned metadata, snapshots, normalization, and bound-artifact hooks provide the extension boundary.
- `DeterministicEvaluationPluginRegistry` validates registration, duplicate/unknown IDs, normalized frozen inputs, applicability, enabled state, and Effect opt-in.
- `TrustedEvaluationPluginRegistry` binds code-owned implementation and gate/tool authority metadata; candidate configuration cannot register executable code.
- Disabled required gates remain `INCOMPLETE`; stale source/config/tool identity and incomplete execution cannot pass.
- The minimal custom plugin and trusted-registration paths are covered in `DeterministicEvaluationPluginTests` without central-dispatch edits.

## #49 — legacy adapters

- Built-in registrations cover .NET test, Crap4CSharp, and Mutate4CSharp through the common registry.
- Compatibility adapter kinds and canonical v1 receipt envelopes remain stable; plugin metadata is exposed on `GateRunResult` without altering legacy command-envelope bytes.
- Existing positive/failing/malformed/missing-tool/disabled regressions remain in the Evidence suite.
- No alternate migrated-check dispatcher bypasses registry resolution in `GateRunner`.
- External installed-tool readiness remains subject to the existing capability checks; fixture adapters are not certification of a host installation.

## #50 — pinned anti-slop

- Upstream source is pinned to `13909c9e07590f5a85d93c62712259fe14a883f7`; Oxlint/plugin runtime is pinned to `1.78.0`; licenses, closure digests, setup, and update procedure are in `components/evidence/anti-slop/PROVENANCE.md`.
- Applicability covers `.js`, `.jsx`, `.ts`, `.tsx`, `.mjs`, `.cjs`, `.mts`, and `.cts`, case-insensitively, including mixed-language and ignored-input selection.
- Nineteen generic rules are default; five Effect rules require explicit opt-in. Autofix and target-config replacement are forbidden.
- Real pinned fixtures cover clean, violating, malformed, suppression, direct/batch, generic/Effect, and report-failure behavior with file/rule/location and source/config/runtime identities.
- `AdvisoryAntiSlopComposition` and the `AdvisoryAntiSlopExample` entry point provide the supported executable advisory path from candidate inputs through selection, canonical request construction, `GateRunner`, findings, sidecar, and receipt.

## #51 — cross-plugin proof and onboarding

- Selection tests cover enabled/disabled, applicable/non-applicable, mixed-language, ignored inputs, required/advisory behavior, and Effect opt-in.
- Negative tests cover duplicate/unknown registration, required-disabled checks, missing/malformed/truncated/timeout output, changed source/config/tool identity, incomplete scope, and bounded capture/process settlement.
- Exact-head Linux and Windows hosted runs through `25e8e39` passed 1,155 tests; later closeout commits require fresh hosted CI before merge.
- The executable entry point is compiled as part of the Evidence project; its supported composition is exercised end to end by `Advisory_composition_selects_mixed_inputs_runs_gate_and_returns_receipt`.
- Plugin authoring, configuration/defaults, provenance/update, receipt compatibility, and authority limitations are documented in `docs/quality-gates.md` and the anti-slop provenance file.

## Explicit limitations and follow-up

- The sample and `AdvisoryAntiSlopComposition` are advisory. Clean raw plugin `PASS` remains final `INCOMPLETE` without production authority.
- `AuthoritativeEvaluationHost` remains unavailable. No separate-principal signer, protected producer, or production consumer activation is delivered here.
- No production delivery pipeline automatically invokes anti-slop. That is a separate future integration and does not resume issue #17.
- The reviewed production runtime closure is Linux x64 GNU. Hosted Windows validates framework/fixture portability but is not an anti-slop production-runtime closure certification.
- Issues should be closed only after the final additive head receives fresh hosted Ubuntu/Windows checks and the parent confirms the acceptance mapping.
