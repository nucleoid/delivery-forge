# Operative coding-agent architecture

This document is the repository-owned architecture and plan override for issues #5–#12. It records
the product direction received on 2026-10-09: Delivery Forge targets generic coding agents. Codex,
Claude Code, and Pi are named runtime profiles; OpenClaw is optional. Development-time orchestration
does not define product behavior.

It supersedes historical issue-plan statements that `sessions_spawn` is the only supported launch,
that direct coding-agent adapters are forbidden, that OpenClaw is the canonical runtime, or that
OpenClaw discovery is package acceptance. Historical comments and evidence remain useful history,
but those statements are not operative acceptance criteria.

## Contract through the remaining slices

- **#5 execution:** freezes adapter id/version and required capabilities; binds request, acceptance,
  completion, each exact control action, opaque runtime identities, exact Git facts, and ownership.
  Pause/stop/resume advance only with accepted capability evidence and a fresh fully bound adapter
  result. Unsupported or unproved capability is `INCOMPLETE`.
- **#6 coordination:** leases and fencing bind generic execution runs. External lock availability
  remains independently capability-proven; no local fake substitutes for it.
- **#7 evidence:** tools run through the selected adapter or a deterministic command runner. Agent
  labels and OpenClaw skill discovery are not evidence.
- **#8 review:** review dispatch is adapter-neutral. Codex, Claude Code, Pi, or optional OpenClaw
  mechanisms qualify only with concrete capability proof and exact patch/head binding.
- **#9 scheduling:** the scheduler selects from an adapter registry and its truthful capability
  matrix. Unsupported pause/stop/resume operations block the affected route.
- **#10 publication:** publication consumes generic run/evidence identities and remains parent-only;
  it does not require an OpenClaw receipt.
- **#11 safety:** deterministic conformance fixtures are separate from live adapter integration.
  Each live proof applies only to its exact adapter/version; missing profiles remain `INCOMPLETE`.
- **#12 packaging:** the skill and entrypoint are host-neutral. Diagnostics report Codex, Claude
  Code, Pi, and optional OpenClaw capabilities without installing, authenticating, or inventing
  unavailable interfaces.

Reusable append-only records, exact Git boundaries, one-writer worktrees, evidence normalization,
review invalidation, scheduling, and publication checks remain in force. Linux and Windows are the
v1 platform targets; macOS remains unclaimed until proven. No release decision is made here.
