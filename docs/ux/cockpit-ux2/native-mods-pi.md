# Native Claude Mod and Pi extension evidence

**Branch:** `ux/claude-mods-pi-native-20261021`  
**Approved base:** `0c8b6b8e2418e4a9333fb49c9be4881feb9fccf0` (`origin/ux/ux2-cockpit-prototypes`)  
**Current implementation source:** `90b0110be22f95e9273f50b3b4729e24b7c01535`
**Prior proof-of-load capture source:** `d2de7f2b0a814a5edc5037d381bed285c6fcc4cb`
**Fixture:** synthetic failed-gate scenario `demo-014`, snapshot 4  
**Authority:** zero; no controller, telemetry, approval, dispatch, or production adapter was connected

## Result

The shared projection, Claude Mod, and Pi extension implement the frozen Overview, Evidence, Usage, and Activity contract. Automated parity is **PASS**: both host adapters consume the same generated synthetic projection, and the suite verifies view facts, native interaction state, retained selection, filtering, responsive list/detail behavior, scrolling, Unicode display widths, theme-token semantics, and in-panel fixture cycling.

At the current source, Claude requests a 30-row / 88-column owned pane and renders a responsive native element tree with a fixture `Select`, real view/evidence controls, theme-safe semantic text, grouped summary/identity/readiness/usage/provenance/activity cards, per-model token/cache/cost rows, ledger reconciliation, synthetic pricing sources, and durable request/attempt identities. Pi retains its stronger keyboard/search/list-detail behavior and adds host-theme hierarchy plus `n`/`p` fixture replay inside the custom component.

Native acceptance is **PARTIAL / BLOCKED**. The three Claude images in the existing ledger are real, reviewed proof-of-load from the earlier `d2de7f2…` source; they are intentionally preserved but are **not current-head visual evidence** for the richer tree. Current-head Claude and Pi capture was prevented when laptop access was explicitly revoked during the follow-up. No old image is relabelled as current proof.

## Verification

- Current source package suite: **98 passed, 0 failed, 0 skipped** with `NO_MODEL=1 FIXTURE=1`.
- Claude strict validator passed on installed Claude Code 2.1.306 for the earlier admitted tree. It has **not** been rerun against `90b0110…` because laptop access was revoked before current-source staging.
- Official `claude plugin test` passed 3 tests for the earlier admitted tree. Current source expands those tests with fixture-select, pricing-source, and request-identity assertions, but the installed official runner has **not** rerun them yet.
- Static source-effect boundary: only command registration, local state/store access, pane open/close/resolve/invalidate, and declared hooks. No model, tool, process, network, filesystem, permission, or authority calls.
- Fixture artifact parity: generated plugin fixture bytes match the authoritative shared projection.
- Legacy Codex/companion behavior remains outside this change.

## Claude native capture procedure

The worktree was cloned beneath a uniquely scoped `<task-owned-prefix>` on the laptop.
The Mod was loaded session-locally. A pre-existing unrelated terminal and all other user windows were preserved; exact host paths and window/process identifiers are retained only in the private task artifact.

The three prior frames came from a fresh fixture-only Claude session showing zero tokens. The pane was focused using the host's normal keyboard chord and switched with the Mod's native keys. All files are 1430x210 crops from a 1483x762 terminal window. The crop metadata is retained in the prior ledger. Cropping retained the complete owned pane while excluding terminal footer content.

Each frame was inspected as pixels, not merely hashed:

- `claude-overview.png`: Overview tab, INCOMPLETE gate/blocker, plan/head/tree facts are visible; no clipping or false-green state.
- `claude-usage.png`: Usage tab, known subtotal, PARTIAL label, and provisional/not-a-bill wording are visible; no clipping.
- `claude-activity.png`: Activity tab and chronological synthetic rows are visible; no clipping.
- No crop exposes a local path, account identifier, authentication data, or private user content.

Full hashes, timestamps, dimensions, crop coordinates, and review outcomes are in `assets/native-mods-pi-d2de7f2/capture-ledger.json`.

These captures predate the richer layout, fixture selector, grouped cards, pricing/request provenance, Pi theme treatment, and in-panel replay. Evidence view/detail behavior is proven by the earlier official host tests and current source-side tests, not by this capture set. No current-head Evidence, recovery, or paired-host screenshot is claimed.

## Pi runtime reconciliation

The bounded existing-installation search found only Node 22.14.0 and 22.19.0 under the active NVM root; the previously recorded Node 22.17.0 directory is absent. Node 22.14.0 has no Pi package. Node 22.19.0 contains `@earendil-works/pi-coding-agent@0.80.7`; a separate user npm prefix contains `@mariozechner/pi-coding-agent@0.70.2`. No installed Pi 1.3.0 package exists in the inspected roots.

The exact 0.80.7 Node/CLI path is viable when isolated correctly: with process-local offline mode, task-owned agent/session directories, and explicit no-extension/no-context/no-session flags, `--help` exited normally in about 3.1 seconds with 9,125 stdout bytes and no stderr. Source inspection explains why ordinary `--help` can still touch startup machinery: this build creates settings/runtime services and resource loaders before printing help. The earlier blank launch is therefore rejected as an environment-isolation failure, not described as a permanent binary hang. A current native session was not launched after this finding because computer access was revoked; no install, upgrade, NVM switch, PATH mutation, auth read, or global repair was attempted.

## Incidents and limitations

1. Claude updated itself from 2.1.303 to 2.1.306 during the native launch without an explicit upgrade command. The captures and validator/test evidence are pinned to the observed 2.1.306 runtime.
2. An initial launch used the wrong directory and produced an unknown command; it was closed and rejected as evidence.
3. In the first correctly loaded session, typing `/forge-cockpit-demo view usage` unexpectedly fell through into a model turn rather than routing as a command with arguments. It was aborted promptly with Escape. Consequently there is no claim that the entire laptop exercise was model-free. The three accepted captures came from a separate fresh session opened with the no-argument command and controlled through native pane keys while the host displayed zero tokens.
4. Pi's installed package/version disagrees with the frozen 1.3.0 target. The exact 0.80.7 metadata path now passes under task-owned isolation, but current native rendering remains unexercised because laptop access was revoked before the admitted launch.
5. Claude Desktop and VS Code were detected but not launched or tested.
6. This is synthetic UX evidence only. It proves neither production integration nor real usage/cost/approval/dispatch authority.

## Privacy repair

The earlier public commit accidentally included a preflight file and report text containing local host paths, a paired-device identifier, and window/process identifiers. The current tree removes that preflight and stores it only under the task-owned private artifacts area; this report uses placeholders. No credential or secret was present. The old bytes remain in ordinary Git history because this branch is not force-rewritten; reviewers should assess the current sanitized tree and the explicit incident record.

## Acceptance statement

- Shared facts and implementation contract parity: **PASS**.
- Claude CLI native rendering for Overview, Usage, and Activity: **PASS only for the prior `d2de7f2…` proof-of-load source; current `90b0110…` pixels pending**.
- Claude Evidence interaction: **PASS by official host test; no native capture**.
- Pi source/type and automated interaction parity: **PASS**, including current theme tokens and native fixture cycling.
- Pi native runtime and paired-pixel parity: **BLOCKED / NOT ACCEPTED** pending restored computer access and an explicit decision whether installed 0.80.7 is an acceptable native evidence target despite the frozen 1.3.0 version expectation.
- Desktop/VS Code Mod compatibility: **UNTESTED**.

