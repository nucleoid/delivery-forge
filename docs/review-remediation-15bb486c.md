# Final-review remediation (`15bb486c`)

Date: 2026-10-21

Protected base: `d34280b7f578acca04299009ca6e2bd53e6f0427`

Reviewed parent: `15bb486cb313f2cb7530d4762c8ca3cdb00079b5`

## Finding dispositions

1. The suppression scanner now fails closed for a non-comment slash directly after `<` or `++`/`--`, recognizes regex expression contexts after `export default` and `extends`, and prevents ASI prefix-keyword handling from reclassifying property or private-field names after `.` or `#`. Matched suppressed/unsuppressed fixtures prove all five concrete pairs with pinned Oxlint 1.78.0: `<`, `export default`, `extends`, property/private-field newlines, and prefix `++`/`--`.
2. Initial plugin receipt binding no longer discovers or hashes the anti-slop runtime closure before claim/deadline handling. One caught async pre-execution snapshot computes the configuration and reviewed runtime identities, validates the pins, caches closure bytes and identities, and reuses them for validation, execution, and receipt metadata. Enumeration checks cancellation and every file read receives the deadline-linked token. Missing/corrupt closure content or native binding persists `INCOMPLETE` through the existing no-execution path; a missing Node executable is rejected by capability/snapshot validation and likewise persists `INCOMPLETE`. No synchronous post-execution closure rehash remains.
3. Real package-local Oxlint 1.78.0 fixtures establish two previously unresolved semantics. BOM followed by hashbang is a parser diagnostic and therefore normalizes to `INCOMPLETE`; it is not treated as a clean or policy-only result. `eslint/no-accumulating-spread` suppresses configured `oxc/no-accumulating-spread`, so the scanner classifies that alias as dangerous coverage. A matched unsuppressed control proves the configured rule executes.

Canonical-v1 receipt schema and command-envelope bytes are unchanged. Anti-slop `PASS` and `NOT_APPLICABLE` remain advisory under v1, and orchestration-invalid evidence remains `ERROR`.

## Pinned Oxlint evidence

The package-local command reported `Version: 1.78.0`. Each suppressed fixture removed the protected diagnostic while its matched control reported it:

- `<` regex: 0 versus 1 protected finding
- `export default` regex: 0 versus 1
- `extends` regex: 0 versus 1
- property/private-field keywords: 0 versus 2
- prefix `++`/`--`: 0 versus 2

The complete fixture integration includes BOM+hashbang and the `eslint/no-accumulating-spread` alias against the package-local pin. BOM+hashbang reports Oxlint's code-less `Invalid Character !` parser diagnostic; the alias removes `oxc(no-accumulating-spread)` while its matched control reports that rule.

## Validation

Every SDK, Node, npm, Oxlint, fixture, build, test, and format command ran in one acquired outer `/usr/bin/flock -w 600 /tmp/delivery-forge-validation-lane.lock`, inside finite `/usr/bin/timeout 7200s` and `systemd-run --user --scope --collect --quiet` with `MemoryMax=1024M`, `MemorySwapMax=128M`, and `CPUQuota=100%`. SDK commands used the read-only `/home/fuego/.openclaw/workspace/artifacts/delivery-forge-fire-off-20261007/issue-3/toolchain/dotnet` at 10.0.401 with `--no-restore`, `-m:1`, `/nodeReuse:false`, `-p:UseSharedCompilation=false`, `-p:BuildInParallel=false`, `--disable-build-servers`, and the persistent build-server environment disabled. No restore, SDK/package installation, global tool installation, or mutation campaign ran.

The exact in-lane validation commands were:

- focused: `dotnet test components/evidence/DeliveryForge.Evidence.Tests/DeliveryForge.Evidence.Tests.csproj --no-restore -c Release --filter "FullyQualifiedName~DeterministicEvaluationPluginTests|FullyQualifiedName~GitRepositoryIdentityReaderTests" -m:1 --disable-build-servers -p:UseSharedCompilation=false`
- pinned runtime: `npm run test:fixtures`, `npm run test:batch`, `npm run test:batch-effect`, and `node test-batches.mjs` in `components/evidence/anti-slop`
- full: `dotnet test DeliveryForge.slnx -c Release --no-restore -m:1 --disable-build-servers -p:UseSharedCompilation=false -p:BuildInParallel=false /nodeReuse:false`
- builds: the same bounded arguments with `dotnet build DeliveryForge.slnx -c Debug` and `-c Release`, plus Release builds of the four CI helper projects
- format: `dotnet format DeliveryForge.slnx --no-restore --verify-no-changes --verbosity minimal`

Results:

- focused RED: 141/143 passed; the two failures were the old synchronous pre/post-rehash expectations. The first real-fixture run also rejected an incorrect BOM+hashbang policy-finding expectation and exposed the actual code-less parser diagnostic.
- focused GREEN: 144/144 passed, including cancellation, BOM+hashbang classification, and durable missing-file/binding/Node `INCOMPLETE` receipts.
- pinned Oxlint 1.78.0: clean, violation, malformed, suppression, five regex/division pairs, BOM+hashbang parser error, and `eslint/no-accumulating-spread` alias controls passed; base batch reported one file/19 rules, Effect batch one file/24 rules, and malformed batch shapes failed closed.
- first full run exposed two timeout-reason regressions (the new post-validator deadline check masked the already-observed tool timeout); after removing that check the exact regressions passed 2/2.
- final unfiltered Release solution: 1,138/1,138 passed (Contracts 148, Coordination 112, End-to-End 118, Evidence 298, Execution 88, Planning 104, Publication 120, Review 58, Scheduling 92).
- final Debug and Release solution builds, and all four Release helper builds: 0 warnings, 0 errors.
- final format verification and `git diff --check`: passed.

The scanner remains a deliberately bounded conservative lexer rather than a full JavaScript parser; unresolved lexical ambiguity yields `INCOMPLETE`. Runtime and configuration closure discovery are cached per successful pre-execution snapshot. The current same-UID host does not claim post-snapshot closure immutability or future-v2 authority.

No push, PR mutation, merge, deployment, rebase, amend, issue creation, queue restart, or external publication is part of this remediation.
