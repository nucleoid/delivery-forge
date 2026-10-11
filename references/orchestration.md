# Scheduling and parent ownership

Scheduling distinguishes semantic prerequisites, conflicting changes, scarce resources, and live dispatch authority. Graphs and checkpoints are data. They cannot create an execution lease, writer, accepted host run, or publication permission.

## Profiles

`Scheduler.CreateContractFixtureAsync` is a simulation with fixture tickets and append-only `fixture-scheduler-checkpoint` records. It has no execution-host port. Independent nodes can share a simulated wave; completed prerequisites also need explicit integration observations. These observations are fixture inputs, never production ownership proof.

`LocalSingleScheduler` prepares one isolated issue under one process-local parent lane, even when agent or CPU slots remain. It checks the original checkout, exact mutable base ref, immutable plan/worker scope and ceiling, and resource reservation before creating an isolated worktree and calling `ExecutionCoordinator.PrepareWorkerAsync`. It does not launch a native agent. Host acceptance and control remain the execution component's responsibility. Missing or uncertain host quiescence retains capacity and prevents new dispatch.

Production coordination remains unavailable through the current Continuum adapter. A fixture wave, process-local lane, local writer file, or serialized checkpoint cannot substitute for live distributed ownership. No local multi-issue batch is supported.

## Dependencies and conflicts

`DependencyGraph.Build` snapshots unique bounded nodes and edges, rejects missing prerequisites, and reports the exact cycle chain. Traversal is iterative and deterministic.

Local dispatch accepts `root` only for the frozen work item's own DAG node. Other nodes must bind an immutable `commit:<full Git object ID>` integration condition. Descriptive conditions and open PR claims require a reconciled plan revision. `GitRepositoryContextReader.IsAncestorAsync` reads exact objects without replacement refs or grafts and rejects shallow/promisor history; it proves ancestry of a pinned commit, while dispatch separately rechecks the fresh branch.

Conflicts serialize case aliases, directories and contained paths, unsafe or unresolved maps, shared owners, migrations, schemas, baselines, and generated outputs without known owners. Disjoint exact paths may share fixture waves. Conflict edges never pretend to be semantic dependencies.

## Budgets, ordering, and recovery

Budgets require identified approved parent policy and explicit measured capacity. Agent, CPU, memory MiB, build, test, mutation, and named exclusive slots remain separate. Reservations are parent-issued objects with idempotent release. Resource release does not refund allocated token/cost ceilings: it is not evidence of unused spend. Time limits are finite per-run bounds; physical command controls and host capability proof remain required for actual execution.

Ready nodes follow risk class and FIFO order. Once the configured finite bypass threshold is reached, FIFO takes precedence over risk so feasible waiting work can progress. Failed/incomplete prerequisites block dependents while independent eligible nodes continue.

Pause and stop prevent new starts immediately. Unstarted fixture tickets are revoked; running or uncertain cleanup retains reservations. Queue reconstruction reads immutable records and live-fact inputs, preserves stopped/live/uncertain states, and returns data without restoring tickets. Existing journals cannot create fresh live reservations. Local resume and cold recovery acceptance are still under development; no first full real-run or production certification follows from preparation or fixture tests.
