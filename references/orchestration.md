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

Pause and stop prevent new starts immediately. Unstarted fixture tickets are revoked; an expired unstarted ticket cannot start, and expired running work cannot report PASS or implicitly free live capacity. Failure/incomplete outcomes and fresh quiescence remain distinct. A control generation prevents a Pause or Stop arriving during Resume from being overwritten by the old paused flag. Running or uncertain cleanup retains reservations.

Local Resume records a non-resumable `resuming` intent before its host call and rechecks fresh base, original request/ceiling, writer, artifacts, child and host observations. Host calls have finite waits; a late pending operation cannot restore authority or release capacity. `ReconcileCleanupAsync` waits for pending control settlement and obtains a fresh bound Stop observation before releasing local resources. Native uncertainty without an actual quiescence proof remains retained.

`RelinquishPausedAsync` revokes the former parent's ticket only after a positively quiesced paused state. `RecoverPausedAsync` adopts the original immutable request under a new parent only when the versioned local journal, exact plan/budget and original finite deadline match, no conflicting prior run remains, and fresh Git/child/host facts plus exclusive writer recovery succeed. This does not launch or duplicate a worker. A stored paused/closed claim alone grants no ticket; a pending `resuming` intent cannot be treated as paused recovery. Closed-run observations use the actual case-sensitive UTF-8 hashed namespace, independently of platform path case comparison.

Cold adoption captures the remaining lifetime already observed at adoption as its new monotonic ceiling. The original stored duration/start/expiry remain unchanged; subsequent wall-clock rollback cannot refill that allowance. `LocalRunTimeBudget` is data-only arithmetic, and the actual scheduler supplies native monotonic elapsed time and current UTC observations. Forward wall expiry remains the stricter bound; expired or invalid adoption is refused.

`ReconcileColdTerminalAsync` is a separate cleanup-only route for a fresh parent with no adopted reservation. It binds the original versioned local journal, request, plan, resource policy and namespace, then requires fresh base/artifact/writer/child observations and an exact-bound host Stop result for a known terminal execution history. It can admit later independent work after proven closure, including after the old dispatch lifetime expired; it never returns a dispatch ticket or resumes the old run. Unknown schemas, active histories, live/unknown/unowned children, foreign host bindings and held writers remain nonpassing. A recovered PID is never ownership proof and is not signalled unless this execution lifetime owns its exact identity. Failed observations leave prior history unreconciled, while their transient observation lane is released without refunding or freeing another reservation.

The supported Reserve→Stop-before-provision path also has cleanup-only reconciliation after a restart. Its exact stopped scheduler journal is a prerequisite, not closure proof. The coordinator requires an explicit `ObserveUnstartedAsync` host capability keyed to the original adapter/run/request, with no fabricated accepted runtime identity. This capability defaults to unsupported/unknown; support for ordinary accepted-run Stop cannot substitute for it. Before and after that fresh evidenced quiescence observation, the coordinator rejects any worker history, owned child, pending control, writer, existing worktree/result path or Git registration for the assigned destination, including a missing-but-still-registered worktree. No prepared/accepted worker record or ticket is created. Uncertain or ambiguous observations leave the store blocked; absence of files or records alone never admits independent work.

RunStore discovery/recovery rejects symlink/reparse ancestors and record/pointer leaves, verifies hashed run namespace and immutable bytes, and enforces finite record and aggregate-byte bounds. These checks are data integrity and local path boundaries, not host authentication or distributed ownership. Fixture reconstruction still returns visibly simulated data without restoring tickets. No first full real-run, actual native agent, production or opposite-family certification follows from the local/fixture acceptance tests; those remain separate configured host/tool gates.
