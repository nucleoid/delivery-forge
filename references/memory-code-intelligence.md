# Memory and code-intelligence boundary

Memory and code-intelligence systems are optional advisory inputs to planning. They are never
execution authority, publication authority, or substitutes for checkout bytes.

## Host responsibility

The future host integration owns capability detection, tool calls, authentication, search limits,
and conversion into the host-neutral `ImportedContextEnvelope`. `DeliveryForge.Planning` itself
does not know OpenClaw, Engram, MCP, provider IDs, host paths, credentials, or private skill text.

The JSON envelope is capped at 256 KiB and 256 entries. It accepts only:

- schema version;
- bounded entries containing kind, opaque portable locator, advisory summary, optional summary
  digest, optional claimed checkout-byte digest, observed invariant UTC `Z` time, and
  stale/truncated/heuristic flags;
- conflicts and limitations.

Unknown fields, absolute host paths, obvious credential material, malformed/duplicate JSON, and
oversized input fail closed. Raw documents, prompts, transcripts, source bodies, credentials, and
private filesystem locations are outside the envelope.

## Evidence semantics

- An empty search means only that the bounded search returned no matches. It degrades honestly
  when optional and blocks readiness when the caller declares imported context required.
- Stale, truncated, heuristic, or conflicting results retain those caveats.
- Imported JSON cannot assert checkout-verification state. Repository claims must carry a
  `git:<repository-relative-path>` locator and be checked against the matching exact Git blob with
  `ImportedContextVerifier.VerifyAgainst`; otherwise they remain labeled unverified. Verification
  is bound to the reader-supplied exact commit and tree, which are retained as a limitation, and
  freeze rejects a verified identity that differs from the draft base. A claimed checkout-byte
  digest or locator mismatch is retained as a conflict and blocks readiness.
- Missing optional context degrades with a recorded limitation.
- Missing context declared required by plan/policy blocks readiness.
- `digest` identifies the imported summary when supplied. `checkoutDigest` is a separate claim used
  only for local exact-byte comparison; neither proves the source system was complete, fresh, or
  authoritative.

Portable frozen plans contain distilled evidence metadata and caveats, not raw private context.
Local host state may retain private retrieval details outside Git and outside public artifacts.
