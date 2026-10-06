# Roadmap

This roadmap validates contracts before adding parallelism or publication power.

1. **Contract design** — package boundaries, state machine, mode authorization, manifest/receipt schemas, capability and failure semantics.
2. **Planning integration** — minimal default/deep opt-in interview and frozen plan with provenance/change map/dependencies/tests/rollout/unknowns.
3. **Single worker and checkpoint** — one issue/writer/worktree, durable pause/stop/resume, child-process tracking; no publication.
4. **Continuum coordination adapter** — only after the external lock contract exists; add deterministic lease lifecycle, fencing/idempotency/ACL, uncertainty and quiescence tests.
5. **Normalized .NET evidence** — build/test, Crap4CSharp complexity/coverage, Mutate4CSharp mutation receipts, advisory then reviewed-baseline enforcement.
6. **Independent review and exact head** — cross-family review, patch/head hashing, rebase/edit invalidation rules.
7. **Batch scheduler/resources** — dependency-aware scheduling, integration-required semantics, overlap/contract/migration serialization, bounded expensive commands.
8. **PR and merge publication** — explicit modes, parent-only mutation, exact-head checks, protection awareness, uncertain-result recovery, no implicit deployment.
9. **Recovery and behavior-forward tests** — stale plans, conflicting workers, interruptions, leases, remote drift, incomplete mutation/coverage, baseline gaming, uncertain publish, restart recovery.
10. **Portability/package docs** — thin skill, progressive references, justified helpers, schemas, adapters, compatibility matrix, examples, install/uninstall, security, tests.

Dependencies: 1 precedes all; 2 builds on 1; 3 builds on 1–2; 4 additionally waits for the external coordination contract; 5–6 need stable receipts/single-worker execution; 7 needs 3–6; 8 needs exact-head evidence/review/recovery; 9 exercises every slice; 10 packages proven behavior.
