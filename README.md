# Delivery Forge

**An evidence-driven harness for AI-assisted software delivery.**

Delivery Forge is a planned, portable development harness that turns an approved software-delivery intent into a traceable plan, bounded execution, evidence-backed evaluation, independent review, and an explicitly authorized delivery action.

The design packages a thin skill entry point with progressively loaded references, deterministic helpers where automation is justified, schemas, tests, and replaceable adapters. It does **not** rewrite its own runtime procedure, infer authorization from installed tools, or treat agent confidence as evidence.

## Status

Planning bootstrap only. The contracts and roadmap in this repository describe the intended system; they are not claims that the integrations or quality gates already exist.

## Operating model

```text
understand -> resolve -> plan -> schedule -> implement
           -> evaluate -> review -> deliver -> remember
```

Delivery Forge separates five authorization modes:

- `plan`: investigate and produce a frozen implementation plan; no source change or publication.
- `implement`: execute an approved plan in an isolated writer workspace; no PR or merge authority is implied.
- `pr`: publish the exact evaluated head and open or update a pull request; no merge authority is implied.
- `merge`: merge only the exact reviewed head after required checks and branch protections pass.
- `resume`: reconcile durable local state, Git, live processes, and remote state before choosing a next action.

The normal intake is a minimal interview. Delivery Forge asks one plain conversational question—with a recommendation—only when a genuine intent, risk, or product decision belongs to the user. Engineering questions are resolved from repository evidence when possible. A deep interview is available by explicit opt-in.

## Core principles

- A shared plan artifact records scope, acceptance criteria, source of each decision, exact files/symbols/contracts, prerequisites, test policy, rollout/rollback, unknowns, and exclusions.
- Execution freezes a plan revision hash and base SHA. Drift requires reconciliation, not wishful thinking.
- One writer/worktree owns an issue. Shared contracts, overlapping files, and migrations serialize.
- A parent orchestrator owns the queue, leases, administrative actions, remote publication, and cleanup.
- Opened pull requests are not treated as integrated dependencies.
- Local manifests and receipts are execution authority. Memory stores distilled decisions and outcomes, not raw logs.
- Unsupported tools, timeouts, missing coverage, and incomplete mutation enumeration cannot become a pass.
- PR, CI, merge, release, and deployment are distinct states and authorizations.

## Planned package shape

```text
delivery-forge/
├── SKILL.md
├── references/
│   ├── planning.md
│   ├── orchestration.md
│   ├── worker-contract.md
│   ├── quality-gates.md
│   ├── memory-code-intelligence.md
│   └── recovery-publication.md
├── schemas/
├── scripts/          # deterministic helpers only when justified
├── adapters/
└── tests/
```

This skeleton is planned, not yet implemented or installed.

## Documents

- [Architecture](docs/architecture.md)
- [Operative coding-agent architecture](docs/operative-agent-architecture.md)
- [Plan and execution contracts](docs/plan-contract.md)
- [Quality gates and evidence receipts](docs/quality-gates.md)
- [Recovery and publication](docs/recovery-publication.md)
- [Roadmap](docs/roadmap.md)

## Integration boundary

The design can integrate with a scoped memory system, code intelligence, an atomic leased-lock service, Git hosting, and language-specific evidence tools. Every adapter must detect actual capabilities. An installed CLI is not proof that every planned command, output shape, or guarantee is available.

## Non-goals

Delivery Forge does not modify or fork an agent runtime, permit runtime self-rewriting, make external side effects transactional, let workers weaken policy, or equate labels/checkpoints/open PRs/partial tests with ownership or completion. It never expands a delivery mode into merge, deployment, migration, reauthentication, backfill, or secret changes without distinct authorization.

## License

MIT. See [LICENSE](LICENSE).
