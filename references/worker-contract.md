# Deterministic worker execution contract

Delivery Forge execution is a host-neutral coordination core around separately implemented coding-
agent adapters. It does not contain a model loop, invoke an agent executable, publish remote state,
or infer production support from an installed command or adapter label.

## Adapter boundary

`AgentRequest` freezes the selected adapter id/version, required capabilities, plan/base/request
identity, absolute worktree/result paths, writer token, normalized allow-list, exclusions, and
authorization ceiling. `AgentAcceptedReceipt` must bind that exact request to the same adapter
id/version and opaque runtime run/task identities. Every required capability needs one explicit
`Supported` evidence entry with a concrete interface and evidence reference. `Unsupported`,
`Unverified`, absent, duplicate, or unevidenced capability data is `INCOMPLETE`; it cannot advance
the run.

Repository profiles describe discovered interfaces for Codex CLI, Claude Code CLI, and Pi CLI.
OpenClaw sessions are an optional fourth profile. Profiles are diagnostics, not adapters or proof:
the shipped profiles deliberately remain `Unverified` or `Unsupported` until a live adapter imports
conformance evidence. The current discovery records `codex exec`/`codex resume`, Claude Code
background/logs/stop/resume interfaces, no locally available Pi executable, and optional OpenClaw
session operations. No command is invented for a missing capability.

Completion binds the same adapter version and opaque runtime identities, reports success, and must
agree with a fresh clean descendant head/tree and the path allow-list. Tests/fakes prove only the
deterministic contract. Live proof for one adapter proves only that adapter and version.

## Local authority and recovery

Each run has append-only sequence-and-SHA-256 records. `current.json` is a replaceable pointer, not
authority. Recovery enumerates and hashes immutable records, repairs missing/torn pointers, and
blocks corruption, sequence gaps, or cross-run data. The implementation does not claim directory
metadata fsync durability.

The writer identity file persists across orderly coordinator disposal while its exclusive file
handle is released. A restarted coordinator may recover only the exact run/token; another run,
token, or concurrent owner is rejected. Pause and stop prove writer ownership before signaling or
appending state. Failed preparation abandons its unpublished lock; established runs preserve theirs.

## Fresh observations and scope limits

Pause checkpoints and resume observations re-evaluate issue-5-owned request identity, plan
identity, base commit, authorization ceiling, declared required artifact paths, exact Git
head/tree/status/files, assigned worktree, writer ownership, registered processes, and accepted
adapter activity. The fact source is a port so a caller can supply current owned facts; a mismatch
blocks resume. The base must remain an ancestor of the observed head.

External dependency integration, lease state, and later authorization revocation are owned by
later coordination slices (#6 and #9–#11). The issue-5 core records that limitation and does not
claim to observe those systems. Cleanup eligibility remains a preservation decision only; this
slice exposes no deletion operation or publication authority.

## Process and adapter quiescence

Local task children are registered by PID, platform start identity, executable path, and argv
digest. Signaling requires an exact in-lifetime match. Live or unknown registered process activity
blocks completion. Pause/stop use finite deadlines and preserve every outcome.

An accepted adapter run is independent activity, even when no local task process is registered.
Pause/stop call `IAgentControlPort` with the bound adapter/version/run/request and opaque runtime
identities. Only capability-supported control returning `Quiescent` with an evidence reference can
establish adapter quiescence. Unsupported, unknown, live, or unevidenced activity records
`blocked-quiescence`; an empty local process list never proves an accepted adapter idle.

Linux and Windows local process control are targeted. macOS is unclaimed. Exact local process
control is cooperative safety, not a security sandbox; daemonized or inaccessible activity remains
unknown and blocks.

## Completion and cleanup

`LOCAL_COMPLETE` requires a capability-proven completion receipt, confirmed writer ownership,
clean exact worktree/head/tree, base ancestry, allow-list compliance, and no live/unknown registered
process. Cleanup additionally requires fresh clean boundary/artifact/ownership/process facts and
separate parent authorization. Ambiguous worktrees, user files, unsupported controls, or unknown
activity are preserved, never force-removed.
