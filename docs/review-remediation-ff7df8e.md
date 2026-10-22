# Final-review remediation disposition (`ff7df8e`)

Date: 2026-10-20

Protected base: `d34280b7f578acca04299009ca6e2bd53e6f0427`

Reviewed predecessor: `ff7df8eaa336a6e745c2f28436267df243953461` (`da119e04eb781282c5a376c4b977cd25622d75f4`)

The preserved `.final-review-ff7df8e-output.txt` was read completely and treated as evidence. Each finding was checked against the protected base, predecessor, implementation, and tests.

## Finding dispositions

1. **Suppression scanner — remediated.** The pinned Oxlint closure does not expose a supported standalone parser/comment-token API, so the documented fail-closed lexer path was used. Raw-newline and unterminated string/template/regex states fail closed; JSX contractions, regexes after control conditions, statement-leading regexes, comments inside literals, template expressions, and moved directives have focused coverage.
2. **Frozen bytes versus working tree — remediated.** Production scope derivation rejects `skip-worktree`/`assume-unchanged`, requires regular non-reparse selected inputs, streams each file through Git's exact blob hash algorithm, and compares it with the frozen tree object id before Oxlint runs. Base/head suppression reads resolve object ids and use `git cat-file blob`; no text conversion participates.
3. **Authoritative `NOT_APPLICABLE` — remediated.** N/A now requires the same pinned capability, protected required-gate binding, non-fixture capability, and code-owned production executor/repository reader as PASS. Otherwise it is persisted as `INCOMPLETE`/advisory.
4. **Registry injection — remediated.** Gate execution validates plugin and authority registries as one mapping. The authority registry flows through workflow transition evidence, reference validation, publication definitions, and publication preflight. Custom-registry acceptance and built-in mismatch rejection are tested.
5. **Protected ignores — remediated.** Deleted/renamed protected ignores become identity-bound retired entries instead of deadlocking. Explicit protected ignores may exempt non-regular JavaScript-family entries; unignored non-regular inputs remain fail-closed.
6. **Scale and cancellation — remediated with the bounded-executor alternative.** Scope and execution consume one finite gate deadline. Git captures have an explicit 64 MiB per-command ceiling and a 30-second per-command ceiling inside that shared deadline. Blob reads use exact object ids. The existing code-owned Oxlint host batches argv at 24,000 bytes. A persistent `cat-file --batch` process was not introduced because the command abstraction has no trusted stdin channel; the shared deadline and bounded captures provide the permitted bounded equivalent. Very large repositories that exceed the documented ceiling remain `INCOMPLETE`, not partially accepted.
7. **Readers-first v2 rollout — remediated.** `emitAuthoritativeV2` defaults to `false`; v2 is explicit. Tests cover the unchanged readable v1 default and explicit v2 emission. The v1 schema/fixture/serializer files were not changed by this remediation.
8. **CI authority — remediated as an explicit external issuer boundary.** Built-in CI authority is pinned to tool version `github-actions-v1` and format `github-checks-v1`. Delivery Forge does not claim an in-tree CI producer: a trusted host must translate authenticated exact-head GitHub Actions/Checks evidence and append it to the host-owned store. Repository/workflow artifacts alone have no authority.
9. **Runtime closure hashing — remediated.** Closure files, including Node, are streamed in 64 KiB chunks; fixture-only `tsx` and `typescript` are excluded from runtime authority; missing Oxlint/plugin/native binding/Node assets yield `INCOMPLETE`. Hashes are deliberately recomputed at trust-boundary phases rather than metadata-cached, preserving detection of a closure mutation before/after execution.
10. **Receipt provenance — remediated.** `HostOwnedReceiptStore` uses create-once, write-through, disk-flushed canonical writes under an absolute non-reparse host root and mints an opaque in-memory provenance capability. Workflow transitions, run-manifest reference validation, and publication preflight reject otherwise-valid gate receipt files without that capability. Store replacement and patch-controlled raw-bundle rejection are tested.

## Pin and license evidence

- Anti-slop source pin: `13909c9e07590f5a85d93c62712259fe14a883f7`; plugin version `0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7`.
- Oxlint and `@oxlint/plugins`: exact `1.78.0` dependencies. `package-lock.json` retains registry URLs, SHA-512 integrity values, and MIT metadata for the runtime packages/native bindings.
- Vendored anti-slop license: MIT, copyright Dillon Mulroy, retained at `components/evidence/anti-slop/vendor/anti-slop/LICENSE`.
- No restore, install, global install, network publication, provider, lease, queue, push, merge, or deployment action was performed.

## Resource wrapper

Every SDK, test, build, format, Node, and Oxlint invocation used this outer shape (with the payload replacing `<payload>`):

```text
/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock \
  /usr/bin/timeout <finite-seconds>s \
  /usr/bin/systemd-run --user --scope --collect --quiet \
    -p MemoryMax=1024M -p MemorySwapMax=128M -p CPUQuota=100% \
    /usr/bin/env DOTNET_CLI_HOME=/tmp/delivery-forge-dotnet-home \
      DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
      DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1 \
      <payload>
```

The SDK payload used `/home/fuego/.openclaw/workspace/artifacts/delivery-forge-fire-off-20261007/issue-3/toolchain/dotnet/dotnet`, `--no-restore`, `-m:1`, `/nodeReuse:false`, and `/p:UseSharedCompilation=false` where supported. Format used environment `UseSharedCompilation=false` because `dotnet format` rejects MSBuild `/p:` arguments. Fixture-helper environment values pointed only at this worktree's already-built Debug artifacts.

Process/scope state was inspected before and after validation. The unfiltered and final broad solution runs each left a task-owned Roslyn compiler server in their collected scope despite the disabled-server flags; each exact transient scope was stopped after its test process completed. Build, format, and Oxlint fixture runs did not leave task-owned processes, and the final process check was clean.

## Validation results

All commands below were payloads inside the wrapper above.

- **Focused RED/GREEN and final security set:**
  ```text
  dotnet test components/evidence/DeliveryForge.Evidence.Tests/DeliveryForge.Evidence.Tests.csproj --no-restore --no-build --nologo -m:1 /nodeReuse:false /p:UseSharedCompilation=false --filter "FullyQualifiedName~DeterministicEvaluationPluginTests|FullyQualifiedName~GateRunnerTests|FullyQualifiedName~GitRepositoryIdentityReaderTests|FullyQualifiedName~ProcessCommandExecutorTests"
  ```
  Final result: **82 passed, 0 failed**. Earlier focused RED runs reproduced compile/test failures while the remediation was being wired; the corresponding final GREEN sets passed.

- **Authority consumers:**
  ```text
  dotnet test components/contracts/DeliveryForge.Contracts.Tests/DeliveryForge.Contracts.Tests.csproj --no-restore --no-build --nologo -m:1 /nodeReuse:false /p:UseSharedCompilation=false
  dotnet test components/publication/DeliveryForge.Publication.Tests/DeliveryForge.Publication.Tests.csproj --no-restore --no-build --nologo -m:1 /nodeReuse:false /p:UseSharedCompilation=false
  ```
  Final results: **140/140 contracts**, **120/120 publication**.

- **Real pinned Oxlint fixtures:**
  ```text
  /usr/bin/node node_modules/tsx/dist/cli.mjs test-fixtures.ts
  ```
  Run from `components/evidence/anti-slop`; result: **clean PASS, violation FAIL, malformed INCOMPLETE, historical suppression attack reproduced**, exit 0. This executed package-local Oxlint `1.78.0`; no install occurred.

- **Full build:**
  ```text
  dotnet build DeliveryForge.slnx --no-restore --nologo -m:1 /nodeReuse:false /p:UseSharedCompilation=false
  ```
  Result: **succeeded, 0 warnings, 0 errors**.

- **Format verification:**
  ```text
  dotnet format DeliveryForge.slnx --no-restore --verify-no-changes --verbosity minimal
  ```
  A first attempt correctly rejected an unsupported `/p:UseSharedCompilation=false` formatter argument and identified one whitespace defect. After the whitespace-only correction, the exact command above passed with exit 0.

- **Unfiltered full solution:**
  ```text
  dotnet test DeliveryForge.slnx --no-restore --nologo -m:1 /nodeReuse:false /p:UseSharedCompilation=false
  ```
  Result: **1,023 passed, 5 failed**. All five failures are the shared `QualityInterruptionScenarioTests` setup; the first phase timed out at `QualityInterruptionOrchestrator.cs:173` after 2m22s under the mandatory 1-CPU cap and the other four inherited that same lazy setup exception. Isolating that family reproduced the same 5/5 timeout and did not implicate changed code.

- **Broad solution excluding only that reproduced fixture family:**
  ```text
  dotnet test DeliveryForge.slnx --no-restore --no-build --nologo -m:1 /nodeReuse:false /p:UseSharedCompilation=false --filter FullyQualifiedName!~QualityInterruptionScenarioTests
  ```
  Result: **1,023 passed, 0 failed** across contracts 140, coordination 112, end-to-end 113, evidence 196, execution 88, planning 104, publication 120, review 58, and scheduling 92.

- `git diff --check`: passed.

## Remaining limitations

- The suppression detector is a conservative lexer, not an oxc parser API. Ambiguous unterminated lexical states fail closed; new JavaScript syntax may therefore cause `INCOMPLETE` until recognized.
- Git scope discovery remains bounded capture rather than a streaming listing API, and changed-blob reads use bounded per-object `cat-file` calls rather than one stdin-driven `cat-file --batch` process.
- CI receipt creation remains a documented/validated trusted-host integration responsibility.
- The unrelated quality-interruption fixture cannot complete its five-phase shared setup inside its own work budget under the required 1-CPU validation cap; all other solution tests pass in the same lane.
