# Claude Mods + native Pi implementation preflight

**Prepared:** 2026-10-21 UTC  
**Status at preparation:** PREP ONLY — this document records the original readiness checkpoint. The handoff was subsequently declared frozen by the parent, and implementation continued on this same branch. See `docs/ux/cockpit-ux2/native-mods-pi.md` for the implementation and evidence outcome. No production claim is made.

## 1. Isolated branch and exact-base verification

- Repository: `/home/fuego/.openclaw/workspace/projects/delivery-forge`
- Worktree: `/home/fuego/.openclaw/workspace/.worktrees/delivery-forge-claude-mods-pi-20261021`
- New branch: `ux/claude-mods-pi-native-20261021`
- Required remote base ref: `origin/ux/ux2-cockpit-prototypes`
- Required and observed base SHA: `0c8b6b8e2418e4a9333fb49c9be4881feb9fccf0`
- PR verification: open PR #43, head `ux/ux2-cockpit-prototypes` at the same SHA, base `main`
- HEAD subject: `Merge pull request #46 from nucleoid/ux/ux2-capability-matrix-v2`

The remote ref was freshly fetched and compared byte-for-byte with the required SHA before `git worktree add -b` ran. The target worktree path and branch were required to be absent first. No existing worktree was removed, pruned, reset, rebased, force-updated, or cleaned. The main checkout, PR #43 branch, closed PR #33 branch, PR #32, PR #52, and all unrelated dirty state remain outside this worktree.

There is no repository-local `AGENTS.md`; the workspace-level instructions apply.

## 2. Design handoff status

At task start, the design artifact directory contained only the staged research material described in the request. During this preflight, the following files appeared with mtimes around 05:33 UTC:

- `MODS-DESIGN.md` — SHA-256 `0402631141649fd59b3016ddbff6b329abdf6f558b91952c538593062b2e2640`
- `IMPLEMENTER-HANDOFF.md` — SHA-256 `cf9e40016aa98af81affd20795035781abe7e8df00d91a3aaf64469e6268dd47`
- `ISSUE-39-REVISED.md` — SHA-256 `88325f473fa4a188c01656d69534ccb77776f7976d143afeeee9b07b60bdba57`
- `ISSUE-40-REVISED.md` — SHA-256 `3dd6d460029884d2d0f4bb4ca25b49dd94fd1a6956fb4fb2b38a548fd15f0d45`

Their arrival is **not** treated as proof that the Astra handoff is frozen, approved, internally consistent, or assigned for implementation. They were not used to invent a layout or to begin product code. Before implementation, the owner/parent must explicitly establish the frozen handoff and the writer must read those exact bytes completely, reconcile them with the revised issues, and record any replacement hashes.

## 3. Sources read completely

The three staged official research documents were read in full:

- `/home/fuego/.openclaw/workspace/artifacts/delivery-forge-claude-mods-20261021/research/mods-overview.md` (270 lines)
- `/home/fuego/.openclaw/workspace/artifacts/delivery-forge-claude-mods-20261021/research/mods-interface.md` (879 lines)
- `/home/fuego/.openclaw/workspace/artifacts/delivery-forge-claude-mods-20261021/research/mods-reference.md` (416 lines)

The PR #43 package, projection/rendering sources, Claude status-line adapter, Pi extension, UX-2 documentation, and test anatomy were also inspected. Key files include:

- `docs/ux/cockpit-ux2/README.md`
- `examples/presentation-demo/package.json` and `README.md`
- `examples/presentation-demo/src/{fixtures,intents,projector,render-cockpit,render-text,safety,usage,usage-jsonl}.mjs`
- `examples/presentation-demo/hosts/claude/statusline.mjs`
- `examples/presentation-demo/hosts/claude/plugin/.claude-plugin/plugin.json`
- `examples/presentation-demo/hosts/pi/forge-demo-extension.js`
- all eight files under `examples/presentation-demo/test/`

## 4. Current repository/package/test anatomy

The UX-2 code is a private, ESM Node presentation package under `examples/presentation-demo`; it is not a live Forge adapter.

- **Facts/projection:** `data/fixtures.json`, `src/fixtures.mjs`, and `src/projector.mjs` provide 15 deterministic demo/recovery states, strict validation, exact-head/policy/review aggregation, and fail-closed action availability.
- **Shared views:** `src/render-cockpit.mjs` owns cockpit, status, evidence-list/detail, and per-model usage renderers. `src/render-text.mjs` retains legacy text views.
- **Usage:** `src/usage.mjs` and `src/usage-jsonl.mjs` provide synthetic-only normalization, dedupe/revision handling, immutable synthetic rate bindings, and bounded explicit-path JSONL replay. They do not represent actual charge.
- **Claude today:** the package has a standalone `statusLine` process and a skill-only plugin manifest. It has no Claude Mods hooks module yet.
- **Pi today:** `forge-demo-extension.js` registers one `/forge-demo` command, restores branch-local demo cursor state, paints an owned status/widget only in TUI mode, opens bounded focusable detail/usage custom components, and emits non-TUI text with `triggerTurn:false`.
- **Desktop today:** static Claude/Codex mockups and a shared browser panel exist. They are explicitly mockups, not installed-host integration proof.
- **Tests:** pure Node tests cover projection, safety, host adapters, cockpit rendering, Pi component behavior, MCP, usage, and JSONL durability. Playwright tests cover companion/desktop keyboard and responsive behavior.

Baseline executed in this worktree:

```text
NO_MODEL=1 FIXTURE=1 node --test \
  test/claude.test.mjs test/cockpit.test.mjs test/core.test.mjs \
  test/mcp.test.mjs test/pi.test.mjs test/usage.test.mjs
```

Result: **79 passed, 0 failed, 0 skipped**. This excludes the Playwright-backed companion/desktop files; the package documentation reports 87 tests for the full installed dependency/browser suite. No model, host session, network adapter, or product UI was exercised by this baseline.

## 5. Laptop grant and window preservation

All native/API inspection used OpenClaw `cass_computer` node:

`fda9b6b8509e958dc5ad49987eba9659cc527737761adeeaa39d8a56d4663594`

`workspace.info` was called first and confirmed full-computer file/command scope. `windows` was then called before any laptop action. The selected `C:\Users\mstat\source\repos\cass-desktop` checkout was not read, changed, built, or treated as this task's workspace.

The observed unrelated Windows Terminal was preserved unchanged:

- PID `161400`
- handle `61605360`
- title `nucleoid@dappnode: ~`

No window was focused, clicked, typed into, moved, or closed. No Claude, Pi, VS Code, or Desktop interactive session was launched. No global plugin setting, authentication, existing Forge worktree/app/session, or user project file was changed.

One uniquely named temp prefix was used for a static Claude validator fixture only:

`C:\Users\mstat\AppData\Local\Temp\delivery-forge-mods-pi-preflight-20261021-a7f3`

It was never loaded with `--plugin-dir` and contains no product implementation.

## 6. Claude Code 2.1.303: installed readiness

### Version and command availability

The active command is `C:\Users\mstat\.local\bin\claude.exe`; both `claude --version` and file metadata report **Claude Code 2.1.303**.

Installed help proves:

- `claude plugin validate <path>` exists.
  - `--json` emits a machine-readable report.
  - `--strict` turns warnings into exit-1 failures.
- `claude plugin test [dir]` exists.
  - It runs `*.test.ts` and `*.test.tsx` in child processes with `claude-code/testing`.
  - A failed test exits 1.
- `claude --plugin-dir <path>` exists and is session-local/repeatable.
- **No `plugin get-types` command is advertised.** `claude plugin help get-types` returned the generic plugin command list; `get-types` is absent. A bounded search under `.local` and `.claude` found no installed `claude-code.d.ts` copy. Therefore this preflight does not invent a get-types command or claim that an exact installed declaration file was recovered.

### Installed static call-surface proof

After reading validator help, a validator-only temp plugin declared these hooks and calls without loading them into a session. This command passed strict validation:

```text
claude plugin validate . --strict --json
```

The 2.1.303 validator recognized:

- hooks: `session.start`, `command.run{command=forge-preflight-a7f3}`, `ui.render{component=Pane}`, `session.measure`
- calls: `$.command.register`, `$.command.run`, `$.session.usage`, `$.ui.invalidate`, `$.ui.open`, `$.ui.resolve`

This proves recognition by the installed static analyzer. It is **not** runtime rendering proof and does not validate business behavior or argument semantics.

### Relevant documented surface for implementation

The staged official documentation describes the following relevant 2.1.x surface:

- `$.command.register`, `$.command.run`, and `$.command.list`
- `$.ui.resolve(e)` for surface-specific elements; `$.ui.open`/`close` for panes; `$.ui.invalidate('ui.render')` for redraw; plus `focus`, `scroll`, `toast`, `status`, and related UI methods
- `$.session.usage()` returning `startedAt`, `context` (`tokens`, `window`, `percent`), `rateLimits`, and `cost`; related session methods include messages, turns, model, surfaces, version, send, append, and authorize
- relevant lifecycle/data events: `session.start`, `session.end`, `session.measure`, `turn.start`, `turn.step`, `turn.complete`, `tool.call`, `tool.check`, `command.run`, `ui.render`, `ui.press`, `ui.input`, `ui.select`, `ui.focus`, `ui.scroll`, `ui.close`, and `ui.fault`
- relevant render sites: `Pane`, `AbovePrompt`, `UserMessage`, `AssistantMessage`, `ToolUse`, `ToolResult`, `ToolGroup`, `CommandOutput`, `AskUserQuestion`, `Spinner`, `ToolProgress`, `TurnDuration`, `InfoNotice`, `SessionMode`, and `PromptHint`
- common elements: `Box`, `Text`, `Button`, `Link`, `Code`, `Markdown`, `Input`, `Select`, with surface-limited `Svg`, `Client`, `Raster`, and `Image`

Surface differences must remain explicit: VS Code ignores hotkeys and several layout/typography props; `AbovePrompt` is not listed for VS Code; Desktop/VS Code links have tighter URL rules; permissions are not a render site; and malformed element trees fall back to the host drawing.

## 7. Pi: installed source/types and blocker

### What is installed now

The active command resolves to `C:\nvm4w\nodejs\pi.ps1`, which invokes:

`C:\nvm4w\nodejs\node_modules\@earendil-works\pi-coding-agent\dist\cli.js`

Both `npm list -g --depth=0` and that package's `package.json` report **`@earendil-works/pi-coding-agent@0.80.7`**, with Node `>=22.19.0`. The laptop currently has Node `22.19.0` and npm `10.9.3`. The package resolves through the NVM target under `C:\Users\mstat\AppData\Local\nvm\v22.19.0\...`.

This conflicts with the prior UX-2 capture/documentation claim of installed **Pi 1.3.0**. No second Pi/coding-agent package was found in the active global npm root. Until the owner explains the version mapping or restores/verifies the intended 1.3.0 installation, native Pi implementation/capture readiness is blocked. The preflight must not relabel 0.80.7 as 1.3.0.

### CLI help behavior

The installed `dist/cli/args.js` source contains the current help text and advertises, among other flags, `--extension/-e`, `--no-extensions`, `--no-session`, `--no-context-files`, `--offline`, `--help`, and `--version`.

After inspecting that source, a bounded direct `node dist/cli.js --help` check still failed to emit help and remained running; it was stopped. No Pi session was opened, no prompt was sent, and no model/provider call was made. This reproduces the earlier metadata-command hang as a readiness blocker. For this preflight, the CLI surface is sourced from the installed help implementation rather than claiming a successful live help invocation.

### Native API/type surfaces proven from installed declarations

The active 0.80.7 declarations prove:

- `ExtensionAPI.registerCommand(name, { description?, getArgumentCompletions?, handler(args, ctx) })`
- lifecycle events including `session_start` and `session_shutdown`, with explicit startup/reload/new/resume/fork and shutdown reasons
- `ExtensionAPI.appendEntry(customType, data)` for non-LLM session state
- `ExtensionAPI.sendMessage(..., { triggerTurn?, deliverAs? })`; `sendUserMessage` always triggers a turn and is therefore outside the fixture-only display path
- `ExtensionContext.mode` values `tui | rpc | json | print`, `hasUI`, read-only `sessionManager`, model/context usage access, abort/compact, and trust state
- `ExtensionUIContext.setStatus(key, text | undefined)`
- `setWidget(key, string[] | componentFactory | undefined, { placement?: 'aboveEditor' | 'belowEditor' })`
- `custom(factory, { overlay?, overlayOptions?, onHandle? })` for a focusable component returning a result through `done`
- optional `setFooter` and `setHeader` component factories, which are richer native seams but must not be selected until the frozen design and host target are reconciled
- the installed TUI `Component` contract: `render(width): string[]`, optional `handleInput(data)`, optional `wantsKeyRelease`, and required `invalidate()`; `TUI.requestRender(force?)` schedules redraw

The existing UX-2 Pi adapter's `registerCommand`, `setWidget`, `setStatus`, `custom`, `appendEntry`, `sendMessage`, `sessionManager.getBranch`, `session_start`, and `session_shutdown` usages match these active declarations. This is static source/type compatibility only, not certification against Pi 1.3.0.

## 8. Other host presence (not tested)

- Microsoft VS Code is installed at version **1.104.1** (`code.cmd --version`).
- Claude Desktop AppX is installed at version **1.24012.9.0**.

Presence was read from command/package metadata only. Neither host was launched or tested, and no claim is made that its embedded Claude Code build supports the intended mod unchanged.

## 9. Bounded implementation and evidence plan after freeze

1. **Freeze and reconcile inputs.** Read the four handoff/revised-issue files completely, record hashes, resolve contradictions, and confirm that PR #43 SHA remains the approved base. Stop if the base moved or the Pi 1.3.0 discrepancy remains unresolved.
2. **Map ownership before editing.** Keep shared facts/projection/telemetry authoritative in the existing pure modules. Add host adapters rather than duplicating state machines. Claude and Pi may differ natively in layout/interaction, but the same scenario must project the same run, gate, blocker, review, usage, and action facts.
3. **Static call-surface review first.** Review every event/API call and deny model, prompt-submission, process, network, authorization, approval, and global-setting surfaces unless the frozen handoff explicitly requires and bounds one. Run `claude plugin validate --strict --json` before any session-local load. Do not use `--plugin-dir` while static review is failing or incomplete.
4. **NO_MODEL/FIXTURE tests.** Extend pure Node tests and add Claude `claude-code/testing` tests plus Pi test doubles/contract tests. Cover malformed/stale/gap/unknown-cleanup states, exact-head evidence, N/A rationale, keyboard focus, redraw, close/unload cleanup, per-model usage, width bounds, and no consequential side effects. Preserve `triggerTurn:false` for display-only Pi messages.
5. **Session-local native checks only.** After static approval, load the exact worktree plugin for one disposable Claude session with explicit `--plugin-dir`; use an isolated Pi invocation with explicit `-e`, `--no-extensions`, `--no-session`, `--no-context-files`, and offline/no-model controls proven for the corrected target version. Do not install either integration globally.
6. **Real interaction tests.** Exercise actual keyboard focus, Tab/Enter/Escape/hotkeys, scrolling, pane close/reopen, narrow/wide widths, and pointer/click behavior where the host supports it. Test Claude CLI first; report Desktop and VS Code separately because their render-site/element behavior differs. Test Pi's custom component/widget with the exact corrected installed version.
7. **Same-scenario capture set.** Capture Claude and Pi from the same immutable source and fixture/scenario inputs, including at least launch/empty evidence, failed gate, head drift/stale evidence, cleanup unknown/recovery, accepted review, and per-model usage. Any Pi-specific enhancement must not change the underlying facts or weaken blockers.
8. **Pixel and privacy review.** Inspect every frame at target widths/resolutions for clipping, focus, stale/failure salience, color-independent meaning, private paths/account data, terminal chrome, and false-green conditions. Crop only to the documented host surface while retaining enough chrome to prove origin. Reject and recapture defective frames.
9. **Trace ledger.** Record host/version, source SHA/tree, scenario, launch line, isolation flags, capture timestamp, original dimensions, crop rectangle, file SHA-256, reviewer result, incidents, and limitations. Keep harness renders, mockups, and native captures distinctly labelled.
10. **No production claims.** Passing fixtures, validators, type checks, or installed-host rendering do not prove live Forge adapters, production dispatch, real costs, approval authority, or deployment. Publication/issue updates remain parent-owned and require separate authorization.

## 10. Hard exclusions and ownership boundaries

- No product UI or layout implementation before explicit design freeze.
- No generated images or screenshots in this prep.
- No Claude/Pi interactive session and no paid/model prompt.
- No unsafe/bypass flags, installs, upgrades, auth changes, marketplace changes, or global plugin settings.
- No production adapters, live dispatch, auto-approval, authority issuance, or publication behavior.
- No edits to PR #43's branch, closed PR #33, PR #32, PR #52, main, other worktrees, existing host sessions, or the selected Cass Desktop checkout.
- No push, PR, issue edit, merge, force operation, rebase, or commit by default.
- Astra owns the design/handoff artifacts; this branch consumes only a later explicitly frozen handoff.
- This writer owns only this new branch/worktree and the eventual scoped Claude/Pi implementation after authorization.
- The parent/owner retains design-freeze, issue/publication, acceptance, and production-claim authority.

## 11. Current blockers

1. The late-arriving Astra files have not been explicitly declared frozen/approved for implementation.
2. The active Pi installation proves 0.80.7, not the expected 1.3.0; version identity and target declarations must be reconciled.
3. The active Pi metadata/help path hangs; a corrected target must prove bounded `--help`/metadata behavior before native session testing.
4. Claude 2.1.303 advertises validate/test but no `plugin get-types`; exact installed declarations need an approved retrieval method or the implementation must pin its compatibility evidence to validator/tests plus the staged official reference.
5. Claude Desktop and VS Code are present but their embedded/runtime mod versions were intentionally not exercised.

This was the only intended change at the preflight checkpoint. After the parent explicitly declared the design frozen, the same isolated branch/worktree continued into implementation. The historical checks and blockers above are retained rather than rewritten; the implementation evidence document records which blockers were resolved and which remain.
