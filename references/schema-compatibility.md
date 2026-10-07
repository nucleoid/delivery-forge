# Schema compatibility report

Report date: 2026-10-07

| Contract kind | Bundled version | Read | Write | Fixture | Unknown fields |
|---|---:|---:|---:|---:|---|
| `plan` | 1.0.0 | yes | identity/core | valid | rejected |
| `run-manifest` | 1.0.0 | yes | identity/core | valid | rejected |
| `checkpoint` | 1.0.0 | yes | identity/core | valid | rejected |
| `capability-report` | 1.0.0 | yes | identity/core | valid | rejected |
| `gate-receipt` | 1.0.0 | yes | identity/core | valid | rejected |
| `review-receipt` | 1.0.0 | yes | identity/core | valid | rejected |
| `publication-receipt` | 1.0.0 | yes | identity/core | valid | rejected |
| `evidence-policy` | 1.0.0 | yes | identity/core | valid | rejected |
| `replay-bundle` | 1.0.0 | yes | identity/core | valid | rejected |

“Identity/core” means the library provides canonicalization, identity generation, validation, and
append-only persistence. Producers remain responsible for constructing schema fields; there is no
CLI or runtime envelope in this slice.

The reader selects exact `kind@schemaVersion` pairs. It rejects unknown major, minor, and patch
versions rather than interpreting them as 1.0.0. Future additive minors require a bundled schema,
reader update, fixture, and compatibility review. No migration claim exists because there is only
one major version.

Validation evidence:

- one canonical valid fixture per schema;
- invalid fixtures for unknown enum values, missing exact-head fields, and unjustified
  `NOT_APPLICABLE`;
- mutations for unknown fields, stale identities, and source drift;
- RFC 8785 serialization/order vectors and strict UTF-8/number tests.

Platform matrix: Linux local validation and Linux/Windows hosted CI. Windows remains a target—not
an evidence claim for a commit—until the hosted lane passes. macOS is unclaimed.
