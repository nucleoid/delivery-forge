# UX-1.1 clarification — completeness is policy-bound

Astra/xhigh design correction for the still-unpublished `forge.presentation/0.1` proposal, 2026-10-19. This adds no live capability or effect. It clarifies requirements already stated in UX-1: missing evidence cannot become green and UI cannot invent policy.

## Add a requirements projection

Snapshot adds:

`requirements: { gateIds: string[], reviewRequired: boolean, policyIdentity: string }`

These are projected from trusted existing `EvidencePolicyContract.RequiredGates`, `RequireIndependentReview` and the accepted policy identity. The demo carries explicit synthetic equivalents. The renderer cannot edit them. `requirements.policyIdentity` must equal `run.policyIdentity`; absent/invalid requirements are non-PASS. A present receipt's `required` field cannot remove an expected policy gate.

## Aggregate acceptance

- Missing any required gate ID yields INCOMPLETE. Duplicate/conflicting receipt identities are non-PASS. Received extra rows do not satisfy missing requirements.
- An empty/unestablished evidence set at Launch/Plan/Execute is **INCOMPLETE / not yet evaluated**, not PASS. If a future policy legitimately requires zero gates, acceptance must come from an explicit trusted evaluation result and rationale—not array vacuity. That capability is not needed in this demo.
- When review is required, only a current exact-source **PASS** review with accepted trusted independence can contribute to acceptance. INCOMPLETE/unverified/stale/missing review stays INCOMPLETE; FAIL/ERROR preserve their semantics; invalid outcome is ERROR. A string naming a different model family is not proof.
- Never reinterpret gate-set PASS as publication authority or delivery completion.

## Sequence/projection integration

The event reducer validates schema, run identity, event ID, sequence and snapshot consistency. Duplicate conflicts, out-of-order events and gaps must reach the projection's error/continuity state and disable consequential intents. Returning an error list alongside an otherwise-current snapshot is insufficient. Test actual reducer → projection → intent result with malformed/delayed/gapped input.

Public rendering entrypoints safely handle malformed/non-array/missing nested values with bounded ERROR/INCOMPLETE output. They do not throw through a statusline, retain last green, or expose arbitrary text/control sequences.

## Native detail usability

A Pi evidence selection must be visible before Enter. Detail content fits/scrolls within actual available terminal rows; never rely on an over-height component being clipped by the host. Compact widgets render against the provided width, not a hard-coded80 followed by truncation. Invalidation should clear caches, not create a render loop. These are implementation clarifications of UX-1 keyboard/responsiveness, not a new workflow.

## Provenance label

Keep all fixture content DEMO/SYNTHETIC. A standalone/harness screenshot says HARNESS RENDER; the same inert formatter inside actual installed Codex user-shell output, Claude statusline or Pi widget may say NATIVE DEMO SURFACE. This label only describes where pixels are rendered. It must not change capability, source trust, gate acceptance, authority or live-mode admission. Actual installed screenshots still require separate host/version/action receipts.
