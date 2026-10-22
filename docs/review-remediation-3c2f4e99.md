# Final-review remediation disposition (`3c2f4e99`)

Date: 2026-10-21

Protected base: `d34280b7f578acca04299009ca6e2bd53e6f0427`

Reviewed predecessor: `3c2f4e99dde2959521f4507955bffae58dcd041f`

The preserved `.final-review-3c2f4e99-output-skill.txt` was read completely. This remediation is narrow: scanner ambiguity, claim-owned anti-slop closure capture, bounded snapshot memory, captured executable reuse, truthful pre-snapshot metadata, and stale materialization audit metadata.

## Finding dispositions

1. **No-whitespace module strings and multiline JSX text — remediated.** An apostrophe between word characters now fails closed instead of being skipped as a JSX contraction. JSX-text look-behind crosses line terminators, so a backtick in multiline JSX text fails closed rather than opening a false template span. Exact scanner regressions and matched suppressed/unsuppressed pinned-Oxlint fixtures cover both forms.
2. **Factory capture before claim — remediated.** `AntiSlopRequestFactory.Create` performs bounded structural composition and sets configuration identity to `unavailable`; it no longer hashes the closure. `GateRunner` captures and validates the closure once, after claim creation, inside the shared deadline, then replaces the request identity with the captured snapshot identity before execution and persistence.
3. **Memory and executable re-reads — remediated.** The per-snapshot closure cache has a 256 MiB aggregate ceiling. The snapshot records the Node executable digest from cached closure bytes; capability validation before and after execution reuses that digest instead of synchronously reading Node again.
4. **Truthful unavailable and audit metadata — remediated.** Pre-snapshot anti-slop metadata reports configuration/runtime closure identities as `unavailable`. Successful execution binds the same captured configuration identity in the result, v1 command envelope, future-v2 envelope, authority binding, and sidecar. `GateRunResult.Invocation` uses the stable logical frozen-input label rather than a deleted materialization path.

Canonical v1 schema and built-in .NET/Crap4CSharp/Mutate4CSharp behavior are unchanged. Anti-slop remains advisory on the same-UID host; future-v2 authority remains disabled.

## Validation

Every SDK, test, build, format, Node, and Oxlint invocation used one outer `/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock`, a finite `/usr/bin/timeout`, and `systemd-run --user --scope --collect --quiet -p MemoryMax=1024M -p MemorySwapMax=128M -p CPUQuota=100%`. The readonly SDK was `/home/fuego/.openclaw/workspace/artifacts/delivery-forge-fire-off-20261007/issue-3/toolchain/dotnet/dotnet`; restore and installation were not run, and persistent build servers were disabled.

- Focused RED: the two scanner attacks and closure-reading factory test failed for the intended reasons; pinned Oxlint matched both new suppressed/unsuppressed fixture pairs.
- Focused GREEN: 25/25; final security selection 187/187.
- Real pinned Oxlint fixtures: exit 0; both new unsuppressed controls reported `anti-slop(no-chained-type-assertions)`, and both exact directives suppressed that rule.
- Debug solution build: 0 warnings, 0 errors. The exact helper-backed suite passed Contracts 148, Coordination 112, EndToEnd 118, Evidence 301, Execution 88, Planning 104, Publication 120, Review 58, and Scheduling 92: 1,141/1,141 cumulatively on the exact source and rebuilt helper artifacts.
- Release solution, crash driver, lifecycle helper, bounded-capture helper, and import-Git helper builds: 0 warnings, 0 errors.
- `dotnet format DeliveryForge.slnx --no-restore --verify-no-changes --verbosity minimal`: exit 0.

One discarded full-suite attempt omitted the documented root-built helper environment and correctly produced fixture-prerequisite failures. The corrected broad run initially found one stale out-of-solution Debug crash-driver binary; rebuilding that driver made the isolated failure pass 1/1, and the complete EndToEnd project then passed 118/118.
