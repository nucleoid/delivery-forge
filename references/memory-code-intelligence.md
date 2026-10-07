# Memory and code-intelligence boundary

Memory and code-intelligence systems are optional advisory inputs to planning. They are never
execution authority, publication authority, or substitutes for checkout bytes.

## Host responsibility

The future host integration owns capability detection, tool calls, authentication, search limits,
and conversion into the host-neutral `ImportedContextEnvelope`. `DeliveryForge.Planning` itself
does not know OpenClaw, Engram, MCP, provider IDs, host paths, credentials, or private skill text.
Explicit deep intake asks the host for additional bounded evidence, but transports it through the
same repository/policy evidence items and imported-context envelope; no host capability or provider
contract enters the planning library.

The JSON envelope is capped at 256 KiB and 256 entries. It accepts only:

- schema version;
- bounded entries containing kind, opaque portable locator, advisory summary, optional summary
  digest, optional claimed checkout-byte digest, observed invariant UTC `Z` time, and
  stale/truncated/heuristic flags;
- conflicts and limitations.

Unknown fields, arbitrary absolute POSIX/Windows/UNC or tilde/named-home paths, shell/PowerShell/
percent-environment home aliases, single-backslash Windows roots (including
delimiter-adjacent and non-ASCII path segments), URL user-info, obvious key/value, JSON, or
authorization-header credential material (including common secret, password/passwd, and access-key
assignments, general current hyphenated `sk-` API-key forms, `--password`, and curl `--user`, attached `-u`,
or grouped short-option credentials), malformed/duplicate JSON, and
oversized input fail closed. Raw documents, prompts, transcripts, source bodies, credentials, and
private filesystem locations are outside the envelope.

## Evidence semantics

- An empty search means only that the bounded search returned no matches. It degrades honestly
  when optional and blocks readiness when the caller declares imported context required.
- Stale, truncated, heuristic, or conflicting results retain those caveats.
- Deep-intake threshold accounting deduplicates both locators and canonical bound identities/digests
  across evidence and imported context. Only
  non-memory imported entries verified against exact checkout bytes, with no stale, truncated, or
  heuristic flags, can satisfy the additional-evidence threshold.
- Imported JSON cannot assert checkout-verification state. Repository claims must carry a
  forward-slash `git:<repository-relative-path>` locator and be checked against the matching exact Git blob with
  `ImportedContextVerifier.VerifyAgainst`; otherwise they remain labeled unverified. Verification
  binds every meaning-bearing entry field (kind, locator, summary, both digests, observed time, and
  stale/truncated/heuristic flags) plus the reader-supplied exact commit, tree, symlink disposition, and generated-file
  classification. Unsafe/unresolved symlinks and conventionally generated files retain matching safety caveats and cannot
  satisfy deep readiness. Post-verification
  mutation downgrades the record to unverified. The commit/tree are retained as a limitation, and
  freeze rejects a verified identity that differs from the draft base. A claimed checkout-byte
  digest or locator mismatch is retained as a conflict and blocks readiness.
- Missing optional context degrades with a recorded limitation.
- Missing context declared required by plan/policy blocks readiness.
- `digest` identifies the imported summary when supplied. `checkoutDigest` is a separate claim used
  only for local exact-byte comparison; neither proves the source system was complete, fresh, or
  authoritative.

Portable frozen plans contain distilled evidence metadata and caveats, not raw private context.
Local host state may retain private retrieval details outside Git and outside public artifacts.
