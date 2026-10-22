# Final-review remediation disposition (`32b1a5ea`)

Date: 2026-10-20

Protected base: `d34280b7f578acca04299009ca6e2bd53e6f0427`

Reviewed parent: `32b1a5ea8283de5e06341398f1b50b9885a623e0`

The complete `.final-review-32b1a5ea-output.txt`, the protected-base diff,
tracked prior remediation records, and retained validation recipes were read
before editing. Review output files remain untracked.

## Finding dispositions

1. **Scanner/Oxlint divergence — remediated.** Keyword regex state now applies
   only in keyword position; ambiguous `await`/`yield`/`of` are not treated as
   unconditional regex prefixes; property names after `.` cannot alter slash
   grammar; and JSX guesses fail closed. Exact scanner tests and real pinned
   Oxlint fixtures cover `of`, `.in`, `.delete`, `.return`, and the TypeScript
   generic/JSX-guess shape. Oxlint 1.78.0 has no disable-inline-directives pass,
   so the supported fail-closed scanner path remains explicit.
2. **Retry publication and exclusion — remediated.** Every attempt is published
   directly as its immutable nonce directory. No canonical gate directory can
   be reserved by cancellation, timeout, disabled execution, identity failure,
   or a crash window. V2 claims live under a stable host-owned root independent
   of run output directories. Tests cover cross-output exclusion and a disabled
   attempt followed by authoritative PASS in the same logical output scope.
3. **Safe rollout — remediated.** Workflow and publication v2 enforcement have
   explicit activation properties that default off. V1 remains readable and
   accepted before activation. Producer-first activation and consumer-first
   rollback are documented and tested.
4. **HMAC trust boundary — remediated.** Authoritative host construction now
   requires an explicit verified separate-security-principal isolation
   attestation. Same-principal or unverified deployments fail closed. The docs
   explicitly state that same-process/same-uid HMAC is not a security boundary.
5. **Bounded scanning — remediated.** Per-file scan bounds remain 4 MiB and the
   base+head aggregate scan ceiling is 64 MiB. Base and head bytes are both
   counted. Profiles retain compact SHA-256 line anchors rather than full source
   copies and hex-expanded lines. Existing cancellation/deadline checks remain.
6. **Frozen-input TOCTOU — remediated.** The materialization carries the exact
   path→Git-object map. After Oxlint exits, all files are re-enumerated and
   re-hashed with Git blob framing; missing, added, reparse, or changed files
   force `INCOMPLETE`. A swap regression covers this boundary.
7. **Directive inheritance/rules — remediated.** Inheritance uses semantic
   scope/rule plus directive and covered-content anchors, so unchanged moved
   directives work while widened coverage does not. Common unrelated bare rules
   are safe; anti-slop aliases, blanket directives, and unknown bare ambiguity
   fail closed.
8. **Single registry — remediated.** The authoritative host owns one registry;
   the bundle carries it and its deterministic identity into activated
   consumers. Substitution or identity mismatch is rejected.
9. **Git/pins/prerequisites — remediated.** Git runs with no inherited
   environment, replacement objects/lazy fetch/prompts disabled, system/global
   config suppressed, hooks disabled, and fsmonitor disabled. Runtime package,
   lock, config, and batch-host bytes are checked against reviewed constants in
   addition to the closure identity. Missing executable/platform/native/tool
   prerequisites are `INCOMPLETE`; orchestration-invalid identity drift remains
   `ERROR`.
10. **Gate semantics — confirmed/remediated.** Required disabled gates remain
    `INCOMPLETE`; external plugin implementations remain code-owned; and fixture
    authority cannot yield production PASS. V1 schema/fixture bytes are
    unchanged.

## Pins and licensing

- anti-slop upstream `13909c9e07590f5a85d93c62712259fe14a883f7`,
  plugin `0.1.2+13909c9e07590f5a85d93c62712259fe14a883f7`, MIT.
- `oxlint` and `@oxlint/plugins` exactly `1.78.0`.
- fixture-only `tsx` `4.23.12` and TypeScript `7.0.2`.
- Vendored upstream and nested license files are retained; no dependency or
  license changed.

## Validation

All SDK, Node, fixture, and Oxlint operations use the required outer flock,
finite timeout, user systemd scope, 1024 MiB memory/128 MiB swap/one-CPU limits,
readonly retained SDK, and disabled build servers.

- Focused evidence selection: 112/112 passed.
- Full Evidence: 239/239 passed.
- Contracts: 142/142 passed.
- Publication: 120/120 passed.
- Full Release solution test: 1,073/1,073 passed (Contracts 142,
  Coordination 112, End-to-End 118, Evidence 239, Execution 88, Planning 104,
  Publication 120, Review 58, Scheduling 92).
- Full Release solution build: succeeded with 0 warnings and 0 errors.
- `dotnet format --verify-no-changes`: passed.
- Real pinned Oxlint fixtures: clean PASS, violation FAIL, malformed
  INCOMPLETE, eight suppression attacks reproduced; the real pinned batch
  completed with zero diagnostics over its clean input.

## Limitations

- Oxlint 1.78.0 exposes no supported CLI switch that disables inline directives
  and no supported standalone comment-token API in the pinned closure. Lexical
  ambiguity therefore intentionally returns `INCOMPLETE`.
- Windows Job Object/native behavior compiles but is not runtime-tested on this
  Linux host.
- CI receipt production remains an external trusted-issuer responsibility;
  consumer authority stays default-off until that genuine production path and
  isolated signer are deployed.
