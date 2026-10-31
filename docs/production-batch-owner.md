# Coordinated batch owner

The installed `implement --plan ANCHOR --config CONTAINER` route can select a
production batch through the optional `ProductionBatch` configuration. There is
no new command mode or implicit batch expansion. The single-run route keeps its
existing ownership composition. Every newly constructed standalone owner now
requires an empty or absent execution-store namespace; retained prior history
can be inspected as recovery DATA and cannot be reused to create a live owner.

The independently selected container names a bounded list of two to 32 nodes,
one aggregate `BudgetPolicy`, an approved finite `FairnessPolicy`, and a fresh
explicit `StoreRoot`. Each node names its `NodeId`, exact `Plan` and leaf
`Configuration` file pins (`Path`, `Bytes`, `Sha256`), `Demand`, and `RiskClass`.
`NodeId` must exist in that node's actual frozen dependency DAG; it need not be
the issue/work-item spelling. The anchor must be one exact selected plan file
and match the container's independent expected plan/contract/work-item/ceiling.

Every leaf uses an existing standalone-root, host-admission, actual Continuum,
and LocalSingle selection. Leaves cannot select another batch or historical
quality mode. They share the container repository/ref/Git identity and cannot
raise its ceiling. Their original operation interval must equal the container
interval. This initial profile requires each demand's time limit to equal that
same original WORK limit; all actual work uses its decreasing remaining time.
The existing WORK limit is the lesser of 1100 seconds and the operation interval
minus its existing reserve (the lesser of one-quarter of that interval and
60 seconds). No node gets a new clock, cleanup allowance or token/cost budget.

The container's policy must match each leaf's policy. Capacity is measured from
the actual containing caller and allocated once through one shared budget.
Released slots do not refund reserved token/cost ceilings. Current installed
capacity has one intensive lane; real nodes therefore run serially, with actual
coordinated ownership even when changes are disjoint. Issue, branch, workspace
and conservative repository-write leases remain in force. A configuration or a
queue checkpoint cannot establish any of these actual capabilities.

Required semantic prerequisites use frozen `commit:<full Git object ID>`
conditions. The actual branch must contain those commits. An open PR, successful
worker or same-tree change does not establish integration. This initial product
profile refuses prerequisites whose IDs name another selected batch root, even
when the queue kernel could order those roots. Select independent roots with
already-integrated external commit prerequisites instead. The batch cannot
merge an earlier worker head or rewrite a dependent's frozen plan. Such a chain
requires independently authorized integration followed by a newly reconciled
plan/configuration and a fresh batch invocation. Base or candidate
drift leaves a node `ReplanRequired`; a new frozen plan/configuration revision
is needed. Unrelated DAG nodes are not silently made prerequisites.

Worker-local completion is recorded as `LocalComplete` when protected quality
and accepted independent review are absent; it cannot satisfy dependents.
`Pass` additionally requires the actual private protected-quality comparison,
accepted independent-review proof and receipts covering the frozen required
gate identities. Ordinary settled semantic nonpass can leave independent work
eligible. A native/owner fault or unknown operation halts this initial batch
profile rather than granting a fresh failure allowance.

Successful node retirement closes its actual admission, stops renewal, releases
its actual complete lease set, and closes its transport/checkpoint/native
owners under the same remaining WORK interval. It never sends Stop after a
completed worker and never enters final cleanup between nodes. A failed or
uncertain retirement retains the original owners and prevents another node.

`--run-dir`, `--result-dir` and `--replay-out` overrides are refused for this
route: its pinned leaf selections define separate destinations. Cross-node
worker/result/store/artifact/checkpoint namespaces and protected input files
must be separate. Existing files, source history and failed evidence are kept.

`implement --dry-run` imports and checks selected plans without creating a host,
lease, worker or journal. `resume --dry-run --run-dir STORE --config CONTAINER`
reconstructs bounded immutable queue data and always reports `MayDispatch:false`.
Fresh-process native-owner adoption remains unavailable.

An optional `ProductionBatch.ControlPath` selects one plain direct child file of
the fresh batch StoreRoot, excluding reserved `batch-selection.json` and `runs`.
It must be absent at startup. After reading the actual owner-created marker, an
operator may publish one complete UTF8 JSON record (maximum1024bytes):
`{"Kind":"ProductionBatchControl-v1","BatchRunId":"<actual marker RunId>","SelectionIdentity":"<actual marker identity>","Action":"pause"}`.
`stop` is the only other Action. Unknown/duplicate/foreign/malformed input is
refused; no record grants dispatch, policy changes, resume or ownership.

The owner reads this input at serial node boundaries: before selecting a node,
after persisting selected intent, and immediately before constructing its actual
composition/coordination owner. It persists only actually observed sticky flags.
Pause/stop prevents subsequent owner creation after acknowledgement; deleting or
replacing a record never clears a latch and stop cannot be lowered to pause.
A command arriving after a boundary is acknowledged at the next boundary; this
is not instantaneous interruption of an active/prepared worker. There is no
background reader, polling timer, stdin protocol or fresh clock. Dry-run and
history inspection do not interpret commands. A failing root latches Stop before
cleanup; the same actual owner still owns native disposition, and completed
workers do not receive an invented Stop. Queue pause/stop also fences new native
node birth; unsupported native pause/resume is not invented.
If pause races a node already being prepared, the birth fence prevents native
start and conservatively halts the batch with the prepared original retained
for containing-owner cleanup. That race does not create a resumable batch.
Generic LocalSingle controls explicitly refuse bound-original cleanup, pause
and cold adoption. The actual containing host retains those controls and its
private same-owner settlement path; expiry never supplies a new WORK interval.
No queue result grants publication, merge, release or deployment permission.

Source/unit tests and fixture results do not certify this route. The separate
explicit installed two-node acceptance must prove actual worker/tool/reviewer
results, first-node resource disposition while original WORK remains open, and
second-node completion under that same original. It is unavailable until live
coordination, current installed admissions and finite policy prerequisites are
actually satisfied.

Control input is a closed, fail-closed boundary. Operators should write one complete canonical JSON document to a separate fresh file and atomically rename it into the selected ControlPath. A malformed, foreign, oversized, torn, deleted-during-read or unreadable/locked file is a terminal refusal: it does not set or acknowledge a valid control latch, and the containing owner performs its existing finite cleanup. Input is not silently ignored to permit more dispatch. A read acknowledgement records only a valid command actually read at an existing boundary; it does not claim instantaneous active-worker pause.
