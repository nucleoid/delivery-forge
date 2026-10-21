# Final review remediation — 655e4d4a

## Disposition

Every grounded finding in `.final-review-655e4d4a-output.txt` was reproduced or verified against the contracts before remediation.

1. **JSX slash bypass:** slash-leading JSX text now fails closed before regex classification. The exact reviewer input is a pinned Oxlint fixture and a scanner regression.
2. **Blanket disable semantics:** a rule-specific enable no longer closes a blanket disable. Real Oxlint 1.78.0 fixtures cover that behavior plus slash-qualified alias suffix matching.
3. **Authority boundary:** the boolean attestation and all in-process v2 minting were removed. `GateRunner` emits canonical advisory v1 only; `AuthoritativeEvaluationHost` cannot be constructed; the obsolete same-process authoritative composition was deleted. PASS/NOT_APPLICABLE for a required production gate therefore remains `INCOMPLETE` until an external separately privileged producer exists.
4. **Resource ceilings and cleanup:** suppression scanning has per-blob and aggregate logical-line ceilings in addition to byte/cardinality ceilings. Frozen roots use owner sidecars for bounded dead-owner recovery. Claim acquisition scavenges at most 64 non-reparse unpublished claims older than ten minutes without touching fresh concurrent files.
5. **Request composition:** public `AntiSlopRequestFactory` owns normalized inputs, canonical argv or content-addressed manifest selection, environment, configuration identity, and runtime closure. A real Git/Oxlint producer-to-consumer regression uses that public path without copied hashing.
6. **Runtime closure:** reviewed Linux x64 GNU base/effect digests cover package/lock, configs, batch host, vendored rules, all `@oxlint/plugins` and Oxlint package files, the one native binding, and Node bytes. V2 compatibility bindings carry and validate the closure identity.
7. **Crash-safe claims:** metadata is flushed to a same-directory pending file, atomically renamed, and held with an advisory lock. Empty/oversized/malformed unlocked claims are recoverable; live locks and reparse ambiguity remain fail-closed.
8. **Git identity:** production reads reject repository-local filters, includes/includeIf, and `core.attributesFile`; local config bytes are checked across capture and Git receives an explicit null attributes file.
9. **Immutable consumers:** workflow and publication production paths use host-owned immutable disabled policy. Caller evidence cannot activate v2, self-compare a registry, or override provenance. Producer-first activation and consumer-first rollback remain documented.
10. **Outcome taxonomy:** orchestration-invalid source/binding evidence remains `ERROR`; unavailable, disabled, zero-file, ambiguous, or non-authoritative evidence is `INCOMPLETE`; executed allowed N/A requires the same unavailable production boundary and cannot authorize.
11. **Registry:** `TrustedEvaluationPluginRegistry` is the public one-entry-per-gate code-owned extension path, binding implementation and authority metadata together. Repository policy can select but cannot register implementations or commands.
12. **Documentation:** `docs/quality-gates.md` now states the actual disabled boundary, rollout/rollback, complete closure, claim/frozen-root recovery, ceilings, Git restrictions, and deployment prerequisite without claiming a separate principal.

## Pins and provenance

- anti-slop upstream `13909c9e07590f5a85d93c62712259fe14a883f7`, MIT, vendored provenance retained
- private unpublished plugin `0.1.2`
- `oxlint` and `@oxlint/plugins` `1.78.0`
- `tsx` `4.23.12`; TypeScript `7.0.2`
- Effect rules remain explicit opt-in; no autofix or global installation was introduced

## Validation lane and results

Every SDK, Node, Oxlint, fixture, build, test, and format invocation used this outer lane with a finite timeout:

```text
/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock
/usr/bin/timeout <finite>s
/usr/bin/systemd-run --user --scope --collect --quiet
  -p MemoryMax=1024M -p MemorySwapMax=128M -p CPUQuota=100%
/usr/bin/env DOTNET_CLI_HOME=/tmp/delivery-forge-dotnet-home
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
  DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1
  UseSharedCompilation=false <payload>
```

The SDK was always `/home/fuego/.openclaw/workspace/artifacts/delivery-forge-fire-off-20261007/issue-3/toolchain/dotnet/dotnet`; SDK commands used `--no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false` where supported. Format omits the unsupported MSBuild property argument. Root-built fixture paths were explicitly supplied for full tests.

- Release solution build: **0 warnings, 0 errors**.
- Focused evidence/security tests: **133/133 passed**.
- Contracts: **142/142 passed**.
- Publication: **120/120 passed**.
- Real pinned Oxlint fixtures: clean PASS, violation FAIL, malformed INCOMPLETE, and **eleven suppression attacks reproduced**.
- `dotnet format DeliveryForge.slnx --no-restore --verify-no-changes --verbosity minimal`: exit 0.
- Broad Release solution excluding only the pre-existing `QualityInterruptionScenarioTests`: **1,075 passed, 1 failed** on the first final-source run. The one failure exposed a stale crash fixture expecting the deliberately removed in-process v2 provenance sidecar. After removing only that expectation, its exact regression passed **1/1** and the complete affected end-to-end project passed **113/113**. Thus all **1,076** tests in that broad selection are green on final code.
- Unfiltered full Release solution after that fixture correction: **1,081/1,081 passed**, including all five `QualityInterruptionScenarioTests` under the required one-CPU lane.
- `git diff --check`: clean.

## Honest deployment limitation

This repository and host do not provide an enforceable separately privileged gate executor/signer. Production v2 authority is intentionally disabled, no in-process signing path remains, and required production outcomes stay `INCOMPLETE`. Deployment requires an external principal that protects signing material, claims, frozen inputs, and the ledger from the patch process and same uid, followed by producer-first activation of an immutable consumer policy.
