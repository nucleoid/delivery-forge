# Memory and code-intelligence boundary

Memory and code-intelligence systems are optional advisory inputs to planning. They are never
execution authority, publication authority, or substitutes for checkout bytes.

## Host responsibility

The future host integration owns capability detection, tool calls, authentication, search limits,
and conversion into the host-neutral `ImportedContextEnvelope`. `DeliveryForge.Planning` itself
does not know OpenClaw, Engram, MCP, provider IDs, host paths, credentials, or private skill text.

The JSON envelope is capped at 256 KiB and 256 entries. It accepts only:

- schema version;
- bounded entries containing kind, opaque portable locator, advisory summary, optional digest,
  observed invariant UTC `Z` time, stale/truncated/heuristic flags, and checkout-verification state;
- conflicts and limitations.

Unknown fields, absolute host paths, obvious credential material, malformed/duplicate JSON, and
oversized input fail closed. Raw documents, prompts, transcripts, source bodies, credentials, and
private filesystem locations are outside the envelope.

## Evidence semantics

- An empty search means only that the bounded search returned no matches.
- Stale, truncated, heuristic, or conflicting results retain those caveats.
- Repository claims must be checked against exact Git blob bytes with
  `ImportedContextVerifier.VerifyAgainst` or explicitly remain labeled unverified. Digest mismatch
  is retained as a conflict and blocks readiness.
- Missing optional context degrades with a recorded limitation.
- Missing context declared required by plan/policy blocks readiness.
- Digests identify imported summaries when supplied; they do not prove the source system was
  complete, fresh, or authoritative.

Portable frozen plans contain distilled evidence metadata and caveats, not raw private context.
Local host state may retain private retrieval details outside Git and outside public artifacts.
