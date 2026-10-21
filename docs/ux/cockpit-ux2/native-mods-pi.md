# Native Claude Mod and Pi extension evidence

**Branch:** `ux/claude-mods-pi-native-20261021`  
**Approved base:** `0c8b6b8e2418e4a9333fb49c9be4881feb9fccf0` (`origin/ux/ux2-cockpit-prototypes`)  
**Capture source:** `d2de7f2b0a814a5edc5037d381bed285c6fcc4cb`  
**Fixture:** synthetic failed-gate scenario `demo-014`, snapshot 4  
**Authority:** zero; no controller, telemetry, approval, dispatch, or production adapter was connected

## Result

The shared projection, Claude Mod, and Pi extension implement the frozen Overview, Evidence, Usage, and Activity contract. Automated parity is **PASS**: both host adapters consume the same generated synthetic projection, and the suite verifies view facts, native interaction state, retained selection, filtering, responsive list/detail behavior, scrolling, and Unicode display widths.

Native acceptance is **PARTIAL / BLOCKED**. Claude Code rendered the synthetic fixture in its real terminal pane and the captured pixels were reviewed. Pi-native pixel parity could not be accepted because the laptop's active `pi` shim resolves to `@earendil-works/pi-coding-agent@0.80.7`, not the handoff's expected 1.3.0, and its bounded offline launch opened a blank terminal and hung. It was stopped without weakening controls, installing anything, or sending a model prompt.

## Verification

- Package suite: 97 passed, 0 failed, 0 skipped at the implementation checkpoint.
- Claude strict validator: passed on installed Claude Code 2.1.306.
- Official `claude plugin test`: 3 passed, covering no-turn opening, all four views plus Evidence detail, and denied store access.
- Static source-effect boundary: only command registration, local state/store access, pane open/close/resolve/invalidate, and declared hooks. No model, tool, process, network, filesystem, permission, or authority calls.
- Fixture artifact parity: generated plugin fixture bytes match the authoritative shared projection.
- Legacy Codex/companion behavior remains outside this change.

## Claude native capture procedure

The worktree was cloned to the uniquely scoped laptop temp directory
`C:\Users\mstat\AppData\Local\Temp\delivery-forge-claude-mods-pi-native-20261021-6a38df5`.
The Mod was loaded session-locally. The unrelated DappNode Windows Terminal (PID 161400, handle 61605360) and other user windows were preserved.

The three accepted frames came from a fresh fixture-only Claude session showing zero tokens. The pane was focused using the host's normal keyboard chord and switched with the Mod's native keys. All files are 1430x210 crops from a 1483x762 terminal window. The absolute crop was x=239, y=457, width=1430, height=210; relative to the window it was x=16, y=233. Cropping retained the complete pane while excluding terminal footer content.

Each frame was inspected as pixels, not merely hashed:

- `claude-overview.png`: Overview tab, INCOMPLETE gate/blocker, plan/head/tree facts are visible; no clipping or false-green state.
- `claude-usage.png`: Usage tab, known subtotal, PARTIAL label, and provisional/not-a-bill wording are visible; no clipping.
- `claude-activity.png`: Activity tab and chronological synthetic rows are visible; no clipping.
- No crop exposes a local path, account identifier, authentication data, or private user content.

Full hashes, timestamps, dimensions, crop coordinates, and review outcomes are in `assets/native-mods-pi-d2de7f2/capture-ledger.json`.

These captures predate the follow-up commit that made Evidence controls visibly focusable in a short pane. Evidence view/detail behavior is therefore proven by the official native Mod tests and source review, not by this capture set. No Evidence or recovery screenshot is claimed.

## Incidents and limitations

1. Claude updated itself from 2.1.303 to 2.1.306 during the native launch without an explicit upgrade command. The captures and validator/test evidence are pinned to the observed 2.1.306 runtime.
2. An initial launch used the wrong directory and produced an unknown command; it was closed and rejected as evidence.
3. In the first correctly loaded session, typing `/forge-cockpit-demo view usage` unexpectedly fell through into a model turn rather than routing as a command with arguments. It was aborted promptly with Escape. Consequently there is no claim that the entire laptop exercise was model-free. The three accepted captures came from a separate fresh session opened with the no-argument command and controlled through native pane keys while the host displayed zero tokens.
4. Pi's active package/version disagrees with the frozen target and its isolated offline launch hangs. There are no Pi-native screenshots and no native-pixel parity claim.
5. Claude Desktop and VS Code were detected but not launched or tested.
6. This is synthetic UX evidence only. It proves neither production integration nor real usage/cost/approval/dispatch authority.

## Acceptance statement

- Shared facts and implementation contract parity: **PASS**.
- Claude CLI native rendering for Overview, Usage, and Activity: **PASS, bounded to the recorded fixture/runtime/source**.
- Claude Evidence interaction: **PASS by official host test; no native capture**.
- Pi source/type and automated interaction parity: **PASS against the installed declarations/test doubles**.
- Pi native runtime and paired-pixel parity: **BLOCKED / NOT ACCEPTED** pending an owner-approved, non-hanging target runtime with reconciled version identity.
- Desktop/VS Code Mod compatibility: **UNTESTED**.

