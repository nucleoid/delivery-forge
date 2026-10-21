# Native Claude Mod and Pi extension evidence

**Branch:** `ux/claude-mods-pi-native-20261021`
**Approved base:** `0c8b6b8e2418e4a9333fb49c9be4881feb9fccf0` (`origin/ux/ux2-cockpit-prototypes`)
**Capture source:** `ea8606917de64b340c11db63e6cfa46368f4f562`
**Fixtures:** synthetic failed-gate, execute, and repair scenarios
**Authority:** zero; no controller, telemetry, approval, dispatch, or production adapter was connected

## Result

The shared projection, Claude Mod, and Pi extension implement the frozen Overview, Evidence, Usage, and Activity contract. The corrected implementation passes all regression cases from the two implementation reviews: strict width/height bounds, zero-result filtering without stale detail, complete per-model cost identities at 80 columns, non-fabricated ledger identity, correct observed/priced/provisional counts, cache provenance, Unicode/paste search, retained selection, lifecycle reset, and explicit invalid-selection handling.

Claude presents a responsive owned pane with a native fixture selector, semantic hierarchy/cards, Evidence controls, per-model Usage, ledger/pricing/request provenance, source freshness, and recovery state. Pi renders the same facts and scenarios while retaining the richer native keyboard/search/selection/list-detail/scroll experience and in-panel `n`/`p` fixture cycling.

Native acceptance is **READY FOR PARENT REVIEW**. Twelve current-source captures cover failed-gate Overview/Evidence/Usage, per-model and provenance detail, execute provisional usage, and repair recovery on both hosts. Every image was inspected as pixels and checked for cropping/privacy.

## Verification

- Package suite: **103 passed, 0 failed, 0 skipped** with `NO_MODEL=1 FIXTURE=1`.
- Laptop focused source tests: **24 passed, 0 failed**.
- Claude Code **2.1.306** strict validator: **PASS**, zero errors/warnings.
- Official Claude plugin tests: **3 passed, 0 failed**.
- Managed Pi **1.3.0**: version command **PASS** and native extension exercise **PASS** through the explicit managed launcher under process-local isolation.
- Static source-effect boundary: command registration, local state/store access, owned pane UI, and declared hooks only. No model, tool, process, network, filesystem, permission, or authority calls.
- Generated plugin fixture parity matches the authoritative shared projection; line endings are normalized cross-platform.
- Legacy Codex/companion behavior remains outside this change.

## Native procedures

### Claude

The Mod was loaded session-locally in a fresh uniquely named session. Only the no-argument `/forge-cockpit-demo` command was used; all fixture/view/detail/scroll changes used native pane controls. The accepted session displayed zero tokens throughout. Unsupported command arguments remain documented as unsafe because the host may route them to a model prompt; they were not used.

Claude captures are 1040x430 crops beginning at source screen coordinate `(207,333)`. Cropping retains the complete relevant owned-pane region while excluding terminal/account/path chrome and the unrelated notification area.

### Pi

Pi was invoked through the explicit managed 1.3.0 launcher with a process-local task-owned agent directory, offline mode, no context files, no session, and only the owned extension. Interaction used `/forge-demo` plus native keys: view selection, `j`/`k`, scrolling, and `n`/`p` fixture cycling. No provider/model prompt was sent.

Pi captures are 1483x570 crops beginning at source screen coordinate `(127,228)`. Cropping retains the owned component while excluding package and task paths.

## Capture coverage

Claude:

- failed-gate Overview top and readiness state;
- failed-gate Evidence selection/detail;
- failed-gate Usage model rows;
- failed-gate Usage request/pricing/cache provenance after native scrolling;
- execute Usage with provisional valuation;
- repair Usage recovery state.

Pi:

- failed-gate Overview;
- failed-gate Evidence with keyboard-selected detail;
- failed-gate Usage model rows;
- failed-gate Usage request/pricing/cache detail after keyboard navigation;
- execute Usage after native fixture cycling;
- repair Overview recovery state after native fixture cycling.

The exact timestamps, dimensions, crop coordinates, interactions, and SHA-256 hashes are recorded in [`assets/native-mods-pi-ea86069/capture-ledger.json`](assets/native-mods-pi-ea86069/capture-ledger.json). No current image exposes a local path, account identifier, credential, unrelated toast, or unrelated application chrome. Earlier proof-of-load captures remain in history and are not relabelled as current evidence.

## Incidents and limitations

1. Earlier work recorded Claude self-updating from 2.1.303 to 2.1.306; current validation and captures are pinned to observed 2.1.306.
2. Earlier rejected Claude launches opened the wrong directory or a persisted unrelated session. They were closed without accepted interaction and are not evidence.
3. An earlier argument-form Claude command fell through into a model turn and was aborted. The accepted current session is separate, uses only the verified no-argument command plus native UI, and shows zero tokens.
4. Earlier Pi inspection found stale 0.80.7/0.70.2 installations. Current evidence uses the explicitly managed and independently version-verified Pi 1.3.0 launcher; no global PATH/NVM/plugin/auth setting was changed.
5. Claude Desktop and VS Code were not exercised.
6. This is synthetic UX evidence only. It does not prove production integration, real cost, approval, dispatch, or telemetry authority.

## Privacy repair

An earlier public commit contained a preflight file and report text with local host paths and device/process identifiers. The current tree removes that preflight and stores it only in private task artifacts; this report uses no private identifiers. No credential or secret was present. History was not force-rewritten.

## Acceptance statement

- Shared facts and implementation contract parity: **PASS**.
- Claude current-source native rendering and interaction: **PASS; ready for parent pixel review**.
- Pi 1.3.0 current-source native rendering and interaction: **PASS; ready for parent pixel review**.
- Paired scenario/fact parity: **PASS**, with Pi preserving at least Claude's view coverage and adding richer native keyboard/search/list-detail controls.
- Production claims: **NONE**.
- Desktop/VS Code compatibility: **UNTESTED**.
