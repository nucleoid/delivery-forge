# Deterministic worker execution contract

Delivery Forge execution is a deterministic, local coordination core around a separately supported
agent host. It does not contain a model loop, invoke a CLI agent, publish remote state, or claim that
an available executable is a production agent capability.

## Host boundary

`AgentRequest` is created only after validating the frozen plan, absolute worktree/result paths,
normalized repository-relative allowed paths, exclusions, base commit, and writer token. A parent
adapter may submit that request through a capability it actually supports. In OpenClaw that means a
real `sessions_spawn` call by the parent adapter; the deterministic library cannot make or simulate
that call.

The request has a SHA-256 identity over its complete immutable content. The adapter imports an
`AgentAcceptedReceipt` containing that identity plus the schema, plan, base, assigned worktree,
host run id, child identity, acceptance time, and exact `sessions_spawn` capability. Completion is
successful only when its receipt binds the same request, host run, child, assigned worktree, and a
freshly observed descendant head/tree. Tests and fakes prove serialization and state mechanics
only. They are not host acceptance evidence. When no real host receipt exists, the integration
outcome is `INCOMPLETE`.

## Local authority and recovery

Each run has a separate storage namespace and pointer. Append publication is serialized: JSON is
written to a new temporary file, flushed through the file handle, then atomically renamed without
overwrite to its sequence-and-SHA-256 immutable name. `current.json` is an atomically replaced
convenience pointer, never authority; restart enumerates and hashes immutable records and recovers
from a missing or torn pointer. Filename, record run id, sequence, and hash must agree. A corrupt
immutable record or sequence gap blocks recovery. The implementation does **not** claim directory
metadata fsync durability; a filesystem or power loss beyond flushed file contents can lose the
latest rename, which recovery treats as an absent unpublished record rather than fabricating state.

Checkpoints record observations and the next reconciliation requirement, not a command. Resume
re-observes the frozen plan/base, exact Git head/tree/status, worktree boundary, artifacts,
dependencies, authorization ceiling, writer ownership, and task processes. No stored command is
ever executed during recovery.

## Worktree and writer boundary

- one writer token/lock owns a run;
- the worker destination cannot be the parent checkout, nested in it, pre-populated by another Git
  repository/worktree, or reached through a symlink/reparse point;
- pre/post file inventories reject changed paths outside the normalized allow-list;
- `LOCAL_COMPLETE` requires a clean worktree, including no untracked files, and captures exact
  committed `HEAD^{commit}` and `HEAD^{tree}`;
- symlink/reparse-point content, nested repositories, moved worktrees, out-of-scope writes, and
  user/unknown files block completion or cleanup and are preserved.

These checks are safety boundaries for cooperative tooling, **not a security sandbox**. They do not
prevent a hostile process from accessing the host. Enforceable isolation must be supplied and proven
by the host.

## Process identity and quiescence

Task children are identified by PID, platform start identity, executable path, and executable/argv
SHA-256 digest. Registrations are durable coordinator records and pause/stop derive their inventory
only from those records. Signaling requires an exact, positively observed identity registered by this run.
PID-only, reused, stale, inaccessible, or otherwise unknown identities block signaling and cleanup.
Pause and stop use finite graceful and forced deadlines, record every outcome, stop further dispatch
or publication, and preserve the worktree. A failure to quiesce records `blocked-quiescence`; it does
not release ownership.

Linux and Windows are v1 targets. The core starts and controls children created in the current
executor lifetime on both. It signals only the exact registered process and never performs an
unchecked process-tree kill; independently daemonized descendants therefore remain outside proven
control and block when observed as unknown. Windows graceful console termination is best-effort and
falls back to exact-process termination after the finite deadline. Cross-restart argv reconstruction
is host-capability dependent; absent a trusted observation it remains unknown and blocks. macOS is
not claimed.

## Cleanup

Cleanup is only eligible after fresh facts prove matching plan/base, existing assigned worktree,
clean boundary and Git state, valid artifacts, integrated dependencies, unchanged authorization,
confirmed writer ownership, no live/unknown process, and no user/unknown files. Eligibility is not
deletion authority: the parent performs separately authorized cleanup and never force-removes an
ambiguous worktree.
