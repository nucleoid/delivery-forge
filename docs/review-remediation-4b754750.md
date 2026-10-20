# Final-review round-3 remediation (`4b754750`)

Date: 2026-10-20

Protected base: `d34280b7f578acca04299009ca6e2bd53e6f0427`

Starting parent: `4b75475037a45f3c72dade7686299cffd93ab078`
(`2659d82f965c3fa3b7b10847183335f5837edac8`)

The complete `.final-review-4b754750-output-round3.txt`,
`.final-review-4b754750-prompt.txt`, and prior
`docs/review-remediation-09ad186.md` were read before editing. The earlier output
and round-2 artifacts were treated only as failed OAuth diagnostics. None of the
four `.final-review-4b754750-*` files was modified or staged.

The required project-memory search was attempted first with the exact query, but
the local `claude-code-tools` command reported that its `total-recall` MCP server
was unknown. No memory was read or stored.

## Finding dispositions

1. **One production suppression parser — remediated.**
   `JavaScriptSuppressionScanner` is the only production scanner. It makes one
   linear pass over bounded bytes, recognizes complete line/block comment bodies,
   and does not raw-scan strings, templates, regular expressions, or prose. It
   models the pinned Oxlint disable directive forms, normalizes protected and
   blanket coverage as semantic multisets, and rejects only unmatched new or
   widened dangerous coverage. Inherited coverage and narrower/unrelated
   directives remain legitimate. Production Git-path tests and pinned Oxlint
   fixtures cover both round-3 attacks: `*/` in a line-comment body and a
   multiline block directive.
2. **Bounded linear scanning — remediated.**
   The scanner operates directly on `ReadOnlySpan<byte>` without whole-input or
   suffix copies. It caps input bytes, directives, directive-body bytes, rules per
   directive, and bytes per rule. Cancellation and the shared deadline are
   checked by consumed-byte interval. Unterminated/ambiguous constructs and cap
   exhaustion become `INCOMPLETE` through the repository-evidence boundary.
3. **Recoverable claims — remediated.**
   Claims are durable JSON containing PID, Linux boot ID, `/proc/<pid>/stat`
   process-start token, nonce, and acquisition time (exact OS process start
   identity on non-Linux hosts). A bounded takeover loop never steals a verified
   live owner. It atomically preserves a stale claim, persists an interrupted
   `INCOMPLETE`, then permits a later retry. Oversized/malformed ownership also
   persists `INCOMPLETE`. Tests use naturally exiting child processes and never
   kill the test host.
4. **Authoritative v2 host/provenance — remediated.**
   `AuthoritativeEvaluationHost` is the in-tree producer composition and always
   emits v2 through `HostOwnedReceiptStore`. The store durably creates canonical
   receipt bytes plus an identity-indexed SHA-256 ledger entry in a private,
   non-reparse root. A new store/process re-reads the canonical contract and
   ledger hash; copied or tampered bytes do not verify. Contract, evidence, and
   real crash-driver tests exercise restart/process verification. V1 serialization
   stays unchanged/readable and remains non-authoritative. The runbook now uses
   producers-before-enforcing-consumers rollout and consumer-first rollback.
5. **Authoritative anti-slop N/A — remediated.**
   No-execution N/A calls the same plugin authority validator as execution:
   exact Oxlint `1.78.0`/format, plugin registration and version, trusted bundle
   and Node registration, canonical invocation/environment, configuration
   closure identity, supported inputs, capability, required policy, code-owned
   production Git/process execution, v2 emission, and durable provenance. A
   missing/mismatched binding is `INCOMPLETE`; zero files never pass. A real
   production `ProcessCommandExecutor`/`GitRepositoryIdentityReader` N/A fixture
   verifies consumer authority.
6. **Exact frozen bytes — remediated.**
   The redundant mutable-working-tree byte comparison is gone. The lint root is
   written from exact Git blob bytes and each blob is verified in memory with the
   repository object hash. Checkout `autocrlf`, attributes, smudge/LFS behavior,
   skip-worktree, and assume-unchanged cannot alter lint input. Repository
   cleanliness remains an independent identity check.
7. **Batched Git objects — remediated.**
   Production uses one bounded `git cat-file --batch-check` request and bounded
   `git cat-file --batch` payload groups, with NUL-delimited tree parsing for
   tabs/newlines/unicode paths. It caps 100,000 inputs, 512 MiB logical materialized
   bytes, 64 MiB per object, and 16 MiB ordinary batch payloads. Tests cover 44
   objects with weird paths and a constant batch-process count, malformed and
   truncated output, missing objects, cancellation, and count/byte caps.
8. **Process-tree cleanup/deadlines — remediated.**
   Linux creation-owned capture starts a dedicated process group with `setsid`
   and signals the group on timeout/cancellation/unsettled inherited pipes,
   including an exited root with a live descendant. Windows retains Job Object
   ownership. Standard input is bounded-lifetime owner work. The Node batch host
   gives every Oxlint spawn the shrinking remainder of one 15-minute deadline;
   .NET owns the outer Node/Oxlint tree. Manifest, path, batch, output, and
   aggregate diagnostic bounds fail closed.
9. **Real pinned CI fixtures — remediated.**
   CI selects Node `22.20.0`, installs only the exact lockfile with `npm ci`, and
   runs the vendored fixture suite. Local validation used the already-installed
   package-local `tsx`/Oxlint dependencies and performed no install. No mock result
   is presented as real Oxlint execution.

The code-owned extensible plugin/authority registry and compatibility adapters
for .NET/unit, Crap4CSharp, and Mutate4CSharp remain the single truth path.
Required disabled gates remain `INCOMPLETE`; patch data cannot supply commands;
Effect rules remain explicit opt-in.

## Pins and licensing

- Anti-slop source commit:
  `13909c9e07590f5a85d93c62712259fe14a883f7`, upstream version `0.1.2`, MIT.
- Authoritative plugin version:
  `0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7`.
- `oxlint` and `@oxlint/plugins`: exactly `1.78.0`.
- Fixture toolchain: `tsx` exactly `4.23.12`, TypeScript exactly `7.0.2`.
- The upstream and nested licenses remain vendored; no dependency or pin changed.

## Mandatory validation lane

Every SDK, Node, Oxlint, fixture, build, test, and format invocation after the
initial inspection used this single outer wrapper (with the shown finite timeout):

```text
/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock
/usr/bin/timeout <finite-seconds>s
/usr/bin/systemd-run --user --scope --collect --quiet
  -p MemoryMax=1024M -p MemorySwapMax=128M -p CPUQuota=100%
/usr/bin/env
  DOTNET_CLI_HOME=/tmp/delivery-forge-dotnet-home
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
  DOTNET_CLI_TELEMETRY_OPTOUT=1
  DOTNET_CLI_USE_MSBUILD_SERVER=0
  MSBUILDDISABLENODEREUSE=1
  UseSharedCompilation=false
  <payload>
```

The SDK payload was always
`/home/fuego/.openclaw/workspace/artifacts/delivery-forge-fire-off-20261007/issue-3/toolchain/dotnet/dotnet`.
All SDK commands used `--no-restore -m:1 /nodeReuse:false
/p:UseSharedCompilation=false` where supported. Format uses the environment
property because `dotnet format` rejects the MSBuild property argument. No
restore, install, global install, real delivery, provider, lease, queue, PR,
deployment, or remote operation ran locally.

## Exact validation results

- Focused RED command (six suppression/inheritance/literal/CRLF tests): **6/6
  failed for the intended pre-fix reasons**. Both exact attacks were missed,
  inherited coverage and literal/prose text were rejected, and frozen CRLF bytes
  were incorrectly compared with checkout bytes.
- Final focused evidence command:
  `dotnet test components/evidence/DeliveryForge.Evidence.Tests/DeliveryForge.Evidence.Tests.csproj
  --filter 'FullyQualifiedName~GitRepositoryIdentityReaderTests|FullyQualifiedName~GateRunnerTests|FullyQualifiedName~ProcessCommandExecutorTests|FullyQualifiedName~DeterministicEvaluationPluginTests'`:
  **109 passed, 0 failed**.
- Full Evidence project: **223 passed, 0 failed** (final source, 40 seconds).
- Full Contracts project: **140 passed, 0 failed**.
- Full Publication project: **120 passed, 0 failed**.
- Broad solution command:
  `dotnet test DeliveryForge.slnx --no-build --filter
  'FullyQualifiedName!~QualityInterruptionScenarioTests'` with the exact root-built
  helper paths: **1,048 passed, 1 failed**. The failure identified a stale
  crash-driver assumption that v2 receipts lived under legacy `gate/receipts`.
  After the driver was changed to use `AuthoritativeEvaluationHost` and a fresh
  recovery consumer verified the durable ledger, the isolated regression passed
  **1/1** and the complete affected EndToEnd selection passed **113/113**.
  A final Evidence run added and passed the in-memory batch cancellation regression.
  Therefore the same final-code selection is green across **1,050/1,050** tests,
  although it was not repeated as one monolithic command after the isolated fixes.
- Real pinned fixtures, executed as
  `./node_modules/.bin/tsx test-fixtures.ts`: exit 0; clean PASS, violation FAIL,
  malformed INCOMPLETE, and six suppression attacks reproduced.
- Production batch host, executed as
  `/usr/bin/node run-oxlint-batches.mjs --oxlint
  ./node_modules/oxlint/bin/oxlint --config ./oxlint.config.ts --inputs
  ./fixtures/inputs.json`: exit 0, `number_of_files=1`, `number_of_rules=19`, no
  diagnostics.
- `dotnet build DeliveryForge.slnx --no-restore`: succeeded with **0 warnings,
  0 errors**. The bounded-Git helper and crash driver also built independently
  with **0 warnings, 0 errors**.
- `dotnet format DeliveryForge.slnx --no-restore --verify-no-changes` and the
  corresponding crash-driver and final Evidence project commands: exit 0 with
  no changes required.
- `git diff --check`: clean before final validation.

One discarded full-Evidence preflight omitted `DELIVERY_FORGE_DOTNET_HOST` and
failed its real-SDK prerequisite. Its successful retry used the exact host above.
That first retry also exposed a nested fixture `VBCSCompiler` that inherited the
lane lock; the exact compiler-server PID in that completed test scope was
terminated, and `UseSharedCompilation=false` was added to the outer environment.
All later runs settled without a compiler-server or lock survivor.

## Remaining limitations

- Windows Job Object code compiled and its platform-independent contracts passed,
  but Windows runtime behavior was not exercised on this Linux host.
- The five pre-existing `QualityInterruptionScenarioTests` were not rerun; the
  prior remediation record documents that their shared setup exceeds its own
  budget under the mandatory 1-CPU lane. No changed file is in that fixture.
- CI workflow execution itself was not available locally. The real pinned suite
  was run directly from the already-installed exact package lock instead.
- The required memory service was unavailable as described above.
- A preliminary inspection accidentally executed `node --version` outside the
  lane before any build/test/fixture action. It made no repository or environment
  change. Every subsequent Node/Oxlint invocation used the mandatory wrapper.
