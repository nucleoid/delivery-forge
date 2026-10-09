# Memory and code-intelligence boundary

Memory and code-intelligence results are bounded local advisory input. They are never execution,
approval, or publication authority and never substitute for exact checkout bytes.

The host owns capability detection, authentication, search limits, tool calls, and conversion into
`ImportedContextEnvelope`. The planning library has no host-provider integration.

## Local advisory envelope

The JSON envelope is capped at 256 KiB and 256 entries. An entry contains kind, local locator, raw
advisory summary, optional summary and checkout digests, UTC observation time, stale/truncated/
heuristic flags, optional/required status, and an optional typed distilled-meaning code. Conflicts
and limitations are also retained as bounded local-only strings. Unknown fields, duplicate keys,
invalid digests/timestamps, NUL and disallowed control characters, and oversized input fail closed.

Private paths, credentials, internal identifiers, and private prose may exist in this local envelope;
that is why none of those raw strings is a portable/public value. A caller-provided source label or
public-looking locator does not sanitize them.

## Portable projection

Frozen plans receive only typed fixed sentences, exact repository facts, digests, and public or
opaque evidence locators. Raw summaries, conflicts, limitations, and local locators are never copied.
Required meaning without a safe typed distillation blocks readiness. Optional undistilled meaning is
represented by a fixed caveat. Typed distillation records categories such as caller relationship,
policy constraint, repository conflict, or additional repository evidence; it does not reproduce the
private explanation.

An empty search means only that the bounded search returned no matches. Optional absence degrades
honestly; required absence blocks. Stale, truncated, heuristic, and checkout-conflicting states render
fixed deterministic caveats.

## Checkout verification

Imported JSON cannot assert verification. A `git:<repository-relative-path>` claim is checked against
the matching reader-issued blob by `ImportedContextVerifier.VerifyAgainst`. Verification binds every
entry field, exact commit/tree, symlink disposition, and generated-file classification. Locator or
digest mismatch becomes conflict. Unsafe links and generated files cannot satisfy deep readiness.
Mutation after verification downgrades the entry to unverified.

Envelope validity, typed projection, and bounded scanning do not imply completeness, freshness,
approval, intake acceptance, or publication authority.
