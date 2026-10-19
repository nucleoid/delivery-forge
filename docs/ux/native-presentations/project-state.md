# Delivery Forge — project state at design freeze

Snapshot date: **2026-10-19 UTC**. This document records the public repository state used by the UX proposal. It is not a current release-readiness claim.

## Source baselines

| Baseline | Exact revision | Meaning |
|---|---|---|
| Integrated `main` | [`d34280b7f578acca04299009ca6e2bd53e6f0427`](https://github.com/nucleoid/delivery-forge/tree/d34280b7f578acca04299009ca6e2bd53e6f0427) | Merged implementation through PR #29; no native-presentation prototype |
| Documentation PR #31 | [`0b202a7d60a09f3aa5aac0a92e774634cb61fd16`](https://github.com/nucleoid/delivery-forge/tree/0b202a7d60a09f3aa5aac0a92e774634cb61fd16) | Open documentation audit; not merged |
| Implementation PR #32 | [`6fa0d368d272c63ca2059a0a48484ef7d9667641`](https://github.com/nucleoid/delivery-forge/tree/6fa0d368d272c63ca2059a0a48484ef7d9667641) | Inspected branch-only host/package work; production and full installed-profile acceptance unresolved |

Later commits may supersede these observations. The prototype is intentionally pinned to the design-freeze state and does not modify the runtime implementation.

## Integrated versus branch-only

The inspected integrated source includes executable contracts, plan freezing/import, agent-neutral execution, fail-closed coordination, evidence normalization, receiver-bound review, deterministic local scheduling, and publication/recovery primitives.

At the inspected PR #32 revision, source also includes a `DeliveryForge.Tool` CLI, Codex/Claude/Pi ports, host bridges, Continuum checkpoint/recovery integration, installed-quality policy/currentness work, and a narrow Linux owned-process profile. Source presence does not establish production certification, installed-host behavior, or release readiness.

## Contracts the UX must respect

- `RunManifestContract`: run, repository, plan/policy, exact source, ceiling, state, gate receipts, timestamp.
- `PlanContract`: identity, revision, base, scope, and acceptance criteria.
- `CheckpointContract`: run, sequence, state/source, next action, and receipt references.
- `GateReceiptContract`: gate/policy/source binding, command, timestamps, exit code, outcome, reason, artifact hashes, source-change flag, limitations, and N/A rationale.
- `ReviewReceiptContract`: exact source/patch binding, reviewer family, outcome, findings, and artifact hash. A displayed receipt cannot self-authorize.
- `PublicationReceiptContract`: exact source identity, action, remote ref, and result ID. An unknown remote effect requires readback before retry.
- Wire outcomes are `PASS`, `FAIL`, `INCOMPLETE`, `ERROR`, and `NOT_APPLICABLE`.
- Authorization ceilings are `Plan`, `Implement`, `Pr`, and `Merge`; resume is an operation, not a higher ceiling.

The presentation contract in this bundle is **proposed and demo-only**. It is an anti-corruption projection over trusted Forge records and host observations, not a replacement orchestrator or a change to the v1 wire contracts.

## Integration roles

- **Delivery Forge** owns workflow, authority, evidence, and recovery.
- **Engram** supplies repository/snapshot-bound code intelligence when admitted.
- **Continuum memory** supplies scoped context; **Continuum coordination** is separately admitted owner/fence/lease/reconciliation proof.
- **Switchyard** remains optional and readiness-gated under [issue #30](https://github.com/nucleoid/delivery-forge/issues/30).
- **dotnet**, **Crap4CSharp**, and **Mutate4CSharp** produce raw evidence; Forge validates identity, completeness, and policy.
- **GitHub** supplies repository/PR/check/protection facts and separately authorized publication.
- **Codex**, **Claude Code**, and **Pi** are distinct host surfaces, not interchangeable rendering APIs.

## Current limitations

The bundle contains inert synthetic fixtures, pure projection/intent preview code, deterministic harness/companion captures, and 14 separately labelled installed-host captures bound to prototype source `37aab4c…`. Final source `e9eb721…` was independently Node-tested at 70/70 and retains five public renderer views byte-for-byte, but was not rerun in the installed hosts. The bundle does not run Forge, issue authority, invoke models, contact integrations, modify source repositories, publish a real PR, or prove production integration. Harness PNGs remain labelled **HARNESS RENDER** or **COMPANION DEMO** and must not be substituted for native evidence.

Public references: [PR #31](https://github.com/nucleoid/delivery-forge/pull/31), [PR #32](https://github.com/nucleoid/delivery-forge/pull/32), and [issue #17](https://github.com/nucleoid/delivery-forge/issues/17).
