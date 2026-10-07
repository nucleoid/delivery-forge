# Delivery Forge executable contracts

This directory documents contract version `1.0.0`. The JSON Schema 2020-12 files embedded by
`DeliveryForge.Contracts` are authoritative; the C# records are readable projections, not an
alternative wire definition.

## Compatibility and parsing

- Readers select an exact bundled schema from the pair `kind` + `schemaVersion`.
- Version strings use `major.minor.patch`. This reader supports only exact `1.0.0` schemas.
- Unknown versions, kinds, fields, enum values, and missing required fields fail closed. An additive
  minor version is supported only after its complete schema and reader are bundled.
- Inputs must be strict UTF-8 JSON. Duplicate names at any object depth, comments, trailing commas,
  invalid UTF-8, and non-finite binary64 values are rejected before model binding. JCS accepts the
  full finite binary64 domain; schema fields declared as `integer` are additionally bounded to
  `[-9007199254740991, 9007199254740991]` for interoperable exact values.
- Portable contracts cannot contain host-private filesystem paths or secrets. A local executor may
  retain those in its own non-portable state, outside these schemas.

## Immutable identity

Every document has an `identity` formatted as `sha256:<64 lowercase hex characters>`.

1. Parse under the strict rules above.
2. Remove only the top-level `identity` property. Nested properties with that name remain.
3. Serialize as UTF-8 with RFC 8785 JSON Canonicalization Scheme ordering, escaping, and binary64
   number formatting.
4. SHA-256 hash those bytes and prefix the lowercase digest with `sha256:`.

Validation recomputes identity on every read. All serialized fields other than the top-level
identity—including timestamps and path-like strings—therefore affect identity. Ambient host facts
and filesystem metadata do not affect identity unless a schema explicitly serializes them.

Immutable records are stored portably as `sha256-<64 lowercase hex>.json`; the wire identity retains
its `sha256:` prefix. Rewriting an existing identity with different canonical bytes is corruption.
The immutable bytes are flushed before their atomic rename. `manifest-current.json` is the sole
mutable pointer supported by the core and may reference only an immutable `run-manifest` already
present in the same store; replacement is atomic.

## Canonical values

- Modes: `plan`, `implement`, `pr`, `merge`, `resume`.
- Gate outcomes: `PASS`, `FAIL`, `INCOMPLETE`, `ERROR`, `NOT_APPLICABLE`.
- `NOT_APPLICABLE` requires a concrete `notApplicableRationale` permitted by policy.
- A gate cannot be `PASS` if its `sourceChanged` observation is true. Timeouts, unsupported
  capabilities, absent artifacts, and incomplete enumeration use a non-pass outcome.
- Git object fields use lowercase hexadecimal object IDs. Gate, review, and publication receipts
  bind `baseCommit`, `headCommit`, and `treeId`; exact-head evidence is never inferred from a
  mutable worktree.
- Every gate receipt names an immutable evidence-policy identity. A run manifest binds one policy;
  all listed gates must be PASS receipts for that policy, exact base/head/tree, and every required
  gate. `NOT_APPLICABLE` is valid only when that immutable policy explicitly allows the gate.

## State machine and authority

The successful path is:

```text
UNDERSTANDING -> PLANNED -> READY -> EXECUTING <-> PAUSED
  -> LOCAL_COMPLETE -> EVALUATING -> INDEPENDENT_REVIEW
  -> PR_AUTHORIZED -> PR_PUBLISHED -> CI_COMPLETE
  -> HOST_REVIEW_COMPLETE -> MERGE_AUTHORIZED -> MERGED
```

Any nonterminal state may enter `BLOCKED`, `FAILED`, or `STOPPED`; these and `MERGED` are terminal.
Checkpoint sequence numbers strictly increase.

Entering execution requires at least `implement` authority. Receipt-gated authority is capped by
the immutable evidence policy. `PR_AUTHORIZED` requires `pr` authority
and a passing independent-review receipt bound to the current head and tree. `PR_PUBLISHED`
additionally requires that review receipt and a `PR_PUBLISHED` publication receipt.
`CI_COMPLETE` requires a gate receipt, while `HOST_REVIEW_COMPLETE` uses a distinct hosted-review
receipt. `MERGE_AUTHORIZED` and `MERGED` require `merge` authority and their matching passing hosted
review and `MERGED` publication receipts, all bound to the current head and tree. Credentials,
labels, or available tools never raise a ceiling.

Release and deployment states are deliberately absent from v1.

## Contract ownership

This component owns plan, run-manifest, checkpoint, capability-report, gate-receipt,
review-receipt, publication-receipt, evidence-policy, and replay-bundle schemas. Future context,
worker, coordination, and review envelope producers add schemas here through serialized,
owner-reviewed integration. Consumers must use this validator and identity algorithm rather than a
private permissive serializer.

## Platform support

The core targets `net10.0` with SDK `10.0.401` and has no OS-specific API dependency. Linux is
validated locally. Windows is a v1 target and is verified by the repository's Windows CI lane;
do not claim a particular commit as Windows-verified until that hosted lane passes. macOS is not a
v1 support claim.
