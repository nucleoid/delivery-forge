# Roadmap

This roadmap validates contracts before adding parallelism or publication power.

Implementation is paused under [issue #17](https://github.com/nucleoid/delivery-forge/issues/17). The status labels below were checked on 2026-10-13 against `main` at `d34280b`; they distinguish merged core/fixture work from real or production acceptance. See [the maintained status matrix](status.md) for evidence and blockers.

1. **Contract design ([#3](https://github.com/nucleoid/delivery-forge/issues/3)) — integrated/closed.** Package boundaries, state machine, mode authorization, manifest/receipt schemas, capability and failure semantics.
2. **Planning integration ([#4](https://github.com/nucleoid/delivery-forge/issues/4)) — integrated/closed.** Minimal/deep intake, frozen plans, typed provenance, drift handling, and bounded rich-plan import.
3. **Single worker and checkpoint ([#5](https://github.com/nucleoid/delivery-forge/issues/5)) — integrated/closed core.** Generic coding-agent envelopes, one issue/writer/worktree, durable pause/stop/resume, and child-process tracking; no publication.
4. **Coordination ([#6](https://github.com/nucleoid/delivery-forge/issues/6)) — fail-closed core integrated; production blocked.** Lease lifecycle, fencing/idempotency, uncertainty, journal recovery, and quiescence are implemented against honest contract boundaries. A real external deployed lease capability is still absent.
5. **Normalized evidence ([#7](https://github.com/nucleoid/delivery-forge/issues/7)) — advisory core integrated; enforcement incomplete.** .NET/Crap4CSharp handling and mutation-report decoding exist. Current real invocation binding and approved policy/baseline/budgets remain outstanding.
6. **Independent review ([#8](https://github.com/nucleoid/delivery-forge/issues/8)) — core integrated; live profile incomplete.** Exact Git/provenance and protected receiver-chain logic exist. No configured production reviewer is certified.
7. **Scheduler/resources ([#9](https://github.com/nucleoid/delivery-forge/issues/9)) — local/fixture core integrated.** Dependency, overlap, bounded-resource, pause/stop, and recovery behavior exist; distributed production scheduling remains unavailable.
8. **PR and merge publication ([#10](https://github.com/nucleoid/delivery-forge/issues/10)) — fixture core integrated; live mutation incomplete.** Parent-only ceilings, exact-head checks, protection observations, and uncertain-result recovery exist without a certified production publication profile.
9. **Recovery and behavior-forward tests ([#11](https://github.com/nucleoid/delivery-forge/issues/11)) — substantial partial integration.** Lifecycle, protected-review, command, and parent-crash fixtures exist. The complete matrix and first real full profile do not.
10. **Portability/package ([#12](https://github.com/nucleoid/delivery-forge/issues/12)) — not implemented.** Thin skill, helper CLI, compatibility/security/examples, install/uninstall, and packaged golden-path proof remain future work.

Optional agent-neutral Switchyard tool access is a separate readiness-gated backlog item in [#30](https://github.com/nucleoid/delivery-forge/issues/30); it is not an eleventh prerequisite for the existing direct/local path.

Dependencies: 1 precedes all; 2 builds on 1; 3 builds on 1–2; 4 additionally waits for the external coordination contract; 5–6 need stable receipts/single-worker execution; 7 needs 3–6; 8 needs exact-head evidence/review/recovery; 9 exercises every slice; 10 packages proven behavior.
