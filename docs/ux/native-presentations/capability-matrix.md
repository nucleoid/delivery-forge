# Delivery Forge — native capability matrix

Initial research checked **19 October 2026**. Design owner: **openai/gpt-6-astra**, xhigh. These are capability findings, not claims of installed prototype acceptance.

## The decision

**One evidence model; three deliberately different native presentations.** Pi can host an actual custom terminal cockpit. Claude Code CLI can host a persistent scripted status line, with drilldown through ordinary tool output; its Dynamic Workflows view is a genuine but **runtime-owned orchestration view**, not an arbitrary external event renderer. Codex CLI has structured tool output, skills, plans and native task views, but its footer accepts a fixed item vocabulary—not a scriptable Forge widget. Use an honest compact Forge transcript there. Do not fork a host to make screenshots match.

The current OpenAI documentation also describes **MCP Apps conversation panels in ChatGPT desktop/web**. That is a separate, documented rich presentation opportunity; it is not evidence that Codex CLI, the IDE extension, or an older standalone Codex desktop build can embed it. Surface/version admission must be explicit.

### Evidence notation

- **D** — primary documentation inspected at the date above.
- **S** — source inspected at an immutable ref.
- **V** — installed executable version/help observed, without a model call.
- **U** — local installed rendering/behavior not verified.
- **Not found** means no supported public seam established in the cited scope, not a universal impossibility claim.

## Target profile admission

The preflight target profiles were **Codex 0.153.4**, **Claude Code 2.1.302**, **Pi 0.80.7**, and Node **22.19.0**. Native capture later observed Claude Code **2.1.303** after an automatic version drift; that actual version governs the published Claude assets, while 2.1.302 remains historical preflight evidence. Native results bind prototype source `37aab4c…`, not final source `e9eb721…`.

| Laptop target | Pinned compatibility evidence | Admission/result |
|---|---|---|
| Codex CLI **0.153.4** | Tag resolves to commit **`3d2ee51ca2d5db578f328aa75e20aa22c0197c9a`**. Fixed `StatusLineItem` vocabulary confirmed at this version. `chatwidget/input_submission.rs` explicitly special-cases `!command` to execute a local shell command **instead of sending to the model**; `core/src/tasks/user_shell.rs` executes a shell task and returns `Ok(None)` [C9]. | A supported native **user-shell transcript** capture is possible in principle: run the inert text renderer with `!node ...`, then inspect native output expansion. This is NOT a model-selected MCP invocation, assistant response, skill acceptance, or a persistent footer. Startup/auth/policy/output collapse remain installed-test questions. Do not forge a session/replay transcript. |
| Claude Code CLI **2.1.303** actual / **2.1.302** preflight | Official rolling statusline docs say the command runs at session start, accepts JSON stdin, and supports multiline output [A1]. Installed help established the scoped settings/plugin flags used for preflight. | Four no-prompt restart captures verified the compact status line at 2.1.303. Idle refresh was not observed; no expanded skill/usage panel was invoked. Rolling docs remain distinct from closed-source implementation proof. |
| Pi **0.80.7** | Registry metadata for **`@earendil-works/pi-coding-agent@0.80.7`** and matching Git tag point to **`818d67457cdd6b60bce6b121d16b23141c252dd8`** [P5]. Exact types contain `mode: "tui"\|"rpc"\|"json"\|"print"`, `hasUI`, `setWidget`, `setStatus`, `custom`, `registerCommand`, session lifecycle and branch state. | **`ctx.mode === "tui"` is supported in 0.80.7**. No compatibility shim based merely on `hasUI` is needed. Installed help confirms explicit `-e` works with `--no-extensions`, and includes `--no-context-files`. Native keyboard/layout/restore still unverified. |

Capability discovery precedes feature selection: identify exact host+surface+version → check documented API/host policy → negotiate rich UI, statusline, or text → show why a richer tier was unavailable. An absent property/API/policy grant is a **profile-specific fallback**, not “Pi/Claude/Codex unsupported” in general. Renderer capability is separate from Forge launch/pause/stop/resume certification. See [README.md](README.md) for the current no-provider verification boundary and asset status.

## Initial research environment and source provenance (historical)

| Host/surface | Version/reference inspected | Local observation |
|---|---|---|
| Codex CLI | **0.163.0**, tag `rust-v0.163.0`; fixed footer source [C2] | `codex --version`, `--help` V; no interactive run/model call |
| Codex desktop / current ChatGPT desktop Codex mode | Rolling official docs [C3–C5]; installed desktop build unknown | U; not conflated with CLI or legacy app |
| Codex IDE | Rolling official IDE and app-server docs [C6–C7] | U; extension build unknown |
| Claude Code CLI | **2.1.296** installed; rolling official docs checked same day | Version/help V; status-line contract D; UI U |
| Claude Code Desktop / VS Code | Rolling surface-specific docs [A5–A6]; feature minima stated where documented | U; desktop/extension build unknown |
| Pi | `@earendil-works/pi-coding-agent` **1.3.0 source**, commit `e28e708049650d9b5382bb83ef93be04a0322ffa` [P1] | No `pi` executable on PATH; source D/S; installed U |

Pi's inspected source package is under **earendil-works/pi**, reached from the old badlogic/pi-mono URL. The exact 0.80.7 registry record is also **@earendil-works/pi-coding-agent**, not @mariozechner. Source 1.3.0 remains research context; the pinned 0.80.7 declarations control target compatibility.

## Terminal hosts

| Capability | Codex CLI 0.163.0 | Claude Code CLI 2.1.296 | Pi source 1.3.0 |
|---|---|---|---|
| Skill / command entry | Skills and `$skill`/plugin discovery D; built-in slash commands D [C1,C4] | Skills exposed as `/name`; session-only `--plugin-dir` V/D [A2] | Skills/templates plus deterministic `registerCommand` D/S [P1,P2] |
| Native task / plan progress | Built-in plans, `/agents`, `/subagents`, `/ps`; footer `task-progress` from `update_plan` S [C1,C2] | Task panel, `/tasks`; Dynamic Workflows phase/agent drilldown and `/workflows` D [A3,A4] | Minimal core deliberately omits built-in subagent/plan workflow; extension builds purpose-specific presentation D [P1] |
| Persistent custom Forge status | **No arbitrary footer script established.** `tui.status_line` chooses fixed enum items; `run-state` is host state, not Forge state S [C2] | **Yes**: `statusLine` command takes JSON stdin, emits multi-line ANSI text; `COLUMNS`/`LINES`; refresh interval min 1 s D [A1] | **Yes**: `ctx.ui.setStatus`, `setWidget`, custom footer/header, focusable `custom` components S [P2] |
| Lifecycle hooks | SessionStart/End, pre/post tool, prompt, compact, subagent, Stop, Interrupt; trust review required D [C8] | Session/turn/tool/permission/subagent/task/elicitation events; spinner `statusMessage` is transient D [A7] | `pi.on`: session/tool/agent/turn/settlement; initialize session resources in `session_start`, dispose idempotently on shutdown D/S [P1,P2] |
| Structured prompts | Experimental `tool/requestUserInput`; MCP elicitation supported by protocol. UI availability is negotiated D [C7] | Native question/permission UI and MCP elicitation; not a freeform plugin panel D [A4,A7] | `input`, `select`, `confirm`; `custom` for richer TUI; `ctx.hasUI` also true for RPC, so custom TUI must test `ctx.mode === "tui"` S [P2] |
| Arbitrary rich widgets | No general CLI renderer API established. Markdown/tool text is supported; do not invent cards or buttons | No general third-party JSX/widget API established. Status-line text is real. Dynamic Workflows owns its own phases/agents, not an external event bus | Actual custom component rendering and tool/message renderers; keyboard/focus/overlay APIs D/S [P2,P3] |
| Interrupt / resume | `turn/interrupt` requests cancellation; `thread/resume`, CLI resume D/V. Does not prove Forge descendant closure [C7] | Esc/Stop ends current turn; background tasks can remain. Resume session supported V/D. Workflow `p` pauses/resumes; `x` stops; same-session replay semantics documented [A3–A6] | Abort/resume/session lifecycle and persisted branch entries; app reconstructs UI on session_start D/S [P1,P2,P4] |
| External companion | Link/file/tool output; app-server can power **our own client**, not patch installed UI [C7] | Link/static HTML preview in Desktop; external viewer is not a CLI native widget [A5] | Optional viewer independent of Pi TUI; RPC client can render its own UI, not Pi custom terminal components [P4] |
| Text fallback | Primary presentation; compact state stamp + evidence links | Status line + transcript/tool result; plain no-color mode | Render plain messages in print/JSON/RPC paths where custom TUI unavailable |

### Critical distinction: hooks are not the authority layer

Codex's documented hook error/timeout/malformed-result behavior can fail without blocking a tool, and MCP hook availability is not guaranteed at SessionStart [C8]. Claude's task completion and tool-success hooks are host lifecycle facts, not exact-head Forge receipts [A7]. Pi's `agent_end` is not final idle; retries/continuations can follow it; `agent_settled` is the notification boundary [P1]. None proves all owned processes/leases/publication uncertainty have settled. The Forge controller must enforce policy independently.

## Desktop and IDE are separate surfaces

| Surface | Verified documentary capability | Boundary / prototype decision |
|---|---|---|
| Current ChatGPT desktop/web plugin UI | MCP App sidebar, conversation panel, file entrypoints and display modes documented [C3]; portable plugin and compatibility manifest documented [C4] | **D, not installed-tested.** Rich app integration is a later separately admitted profile; no claim that this task embeds a panel in Codex desktop |
| Codex IDE | Chat/tool/plan/review UI; app-server protocol can power custom clients [C6,C7] | No public third-party panel injection seam established. Use skill/tool transcript fallback, or an explicitly separate IDE extension in future |
| Claude Code Desktop | Code-tab tasks/workflows, diff view, file/browser preview, plugin UI; hooks and skills shared with CLI D [A3,A5] | Workflow view is native; browser preview is a browser pane, not a Forge plugin widget. CLI `statusLine` parity not established |
| Claude Code VS Code | Native task/agent map, plan review, skills/hooks configuration; `/tasks` since 2.1.277, workflow toggle since 2.1.284 D [A6] | No arbitrary third-party cockpit injection seam established. Use tool-result fallback; do not claim a CLI status line inside VS Code |
| Pi Desktop / IDE | No first-party desktop/IDE surface verified in inspected docs | Pi terminal extension is the deliverable. RPC embedding is an independent host/client project |

## Dynamic Workflows: what we can honestly borrow

**Anthropic's official Dynamic Workflows documentation** was inspected [A3]. It documents phase groups, task-panel summary, hierarchical agent drilldown, filters, pause/resume, script visibility and reusable commands. Those are concrete purpose-built UX references, not guesses about implementation.

Borrow the hierarchy: one compact progress line → phases → individual evidence/details. Do **not** replace Forge with a Workflow script: scripts have no direct filesystem/shell access or mid-run user input, results may be `null`, and replay semantics are based on agent-call order. They cannot become Forge's exact-head authorization, durable receipt, or cleanup authority. A demo script can call documented `phase()` and `log()` with fixture text and **zero `agent()` calls**, but empty-phase retention in the installed view remains unverified. It is supplementary to the Claude status-line prototype, not a required custom-renderer fiction.

## Prototype admission (this task)

1. **Codex:** repository-owned skill + dependency-free MCP/text endpoint; protocol harness proves only endpoint IO. The same renderer can later be captured through the pinned native `!` user-shell surface with zero model calls. MCP initialization/tool discovery may be tested separately if safe; discovery is not a model tool-call proof.
2. **Claude Code:** executable statusline and session-local settings/plugin. The actual CLI statusline can later be exercised with the fixture file; no `/statusline` generation prompt or skill/model turn. Optional phase/log-only Dynamic Workflow stays supplementary and version-admitted.
3. **Pi:** actual extension uses pinned 0.80.7 APIs and deterministic commands. It can later be loaded explicitly in the installed CLI to capture widget/detail/recovery; stub tests remain separately labelled.
4. All render the **same marked synthetic event stream**. Target at least **three meaningful native states per host**, with precise surface captions. Harness/companion renders are supplementary and never silently replace missing installed acceptance. Source review must precede loading, and no authentication/policy bypass or credentials copying is permitted.
5. Installed rendering was accepted for the 14 source-`37aab4c…` frames listed in the publication manifest. Final source `e9eb721…`, model-driven MCP/skill invocation, production adapters, and live telemetry remain unverified.

## Primary sources

All rolling pages checked 2026-10-19. Immutable public source links are used where available.

- **C1** https://learn.chatgpt.com/docs/developer-commands.md — command list, resume, task/agent UI, `/statusline`.
- **C2** https://github.com/openai/codex/blob/rust-v0.163.0/codex-rs/tui/src/bottom_pane/status_line_setup.rs — closed `StatusLineItem` enum, fixed host-state fields; plus https://learn.chatgpt.com/docs/config-file/config-reference.md .
- **C3** https://developers.openai.com/plugins/build/extensions — explicitly ChatGPT MCP App UI surfaces and availability caveats.
- **C4** https://developers.openai.com/plugins/build/plugins — portable manifest, compatibility manifest, skills, MCP, marketplace and surface-specific installation constraints.
- **C5** https://learn.chatgpt.com/docs/app.md — current desktop surface naming.
- **C6** https://learn.chatgpt.com/docs/codex/ide.md — IDE-specific product surface.
- **C7** https://learn.chatgpt.com/docs/app-server.md — own-client protocol, event lifecycle, experimental user-input APIs, interrupt/resume, plan statuses.
- **C9** Exact target-version sources: https://github.com/openai/codex/blob/3d2ee51ca2d5db578f328aa75e20aa22c0197c9a/codex-rs/tui/src/chatwidget/input_submission.rs (local `!` routing), https://github.com/openai/codex/blob/3d2ee51ca2d5db578f328aa75e20aa22c0197c9a/codex-rs/core/src/tasks/user_shell.rs (execution), and https://github.com/openai/codex/blob/3d2ee51ca2d5db578f328aa75e20aa22c0197c9a/codex-rs/tui/src/bottom_pane/status_line_setup.rs (footer).
- **C8** https://learn.chatgpt.com/docs/hooks.md — lifecycle, trust and failure behavior. Packaging page has narrower wording about hook distribution; use direct supported profile config in later admission, do not infer every plugin/surface supports hook bundling equally.
- **A1** https://code.claude.com/docs/en/statusline.md — script IO, multiline/ANSI, width, refresh, temporary hiding during permissions/help.
- **A2** https://code.claude.com/docs/en/skills.md and https://code.claude.com/docs/en/plugins/manifest-reference.md — skill/plugin packaging; installed help confirms session-only `--plugin-dir` and `--settings`.
- **A3** https://code.claude.com/docs/en/workflows.md — phases, keyboard controls, script API, permission/resume limits; authoring skill minimum 2.1.248.
- **A4** https://code.claude.com/docs/en/interactive-mode.md — terminal interaction/task behavior.
- **A5** https://code.claude.com/docs/en/desktop.md — native Code-tab tasks and Browser pane, shared hooks/skills, differences from CLI.
- **A6** https://code.claude.com/docs/en/vs-code.md — native extension plan/task/agent surfaces and version minima.
- **A7** https://code.claude.com/docs/en/hooks.md — exact lifecycle/IO/statusMessage.
- **P1** https://github.com/earendil-works/pi/blob/e28e708049650d9b5382bb83ef93be04a0322ffa/packages/coding-agent/docs/extensions.md — extension lifecycle, state and mode boundaries.
- **P2** https://github.com/earendil-works/pi/blob/e28e708049650d9b5382bb83ef93be04a0322ffa/packages/coding-agent/src/core/extensions/types.ts — exact UI/command/context signatures.
- **P3** https://github.com/earendil-works/pi/blob/e28e708049650d9b5382bb83ef93be04a0322ffa/packages/coding-agent/docs/tui.md — components and keyboard rendering.
- **P4** https://github.com/earendil-works/pi/blob/e28e708049650d9b5382bb83ef93be04a0322ffa/packages/coding-agent/docs/rpc.md — external-client protocol, not TUI embedding.
- **P5** https://registry.npmjs.org/@earendil-works/pi-coding-agent/0.80.7 ; https://github.com/earendil-works/pi/blob/818d67457cdd6b60bce6b121d16b23141c252dd8/packages/coding-agent/src/core/extensions/types.ts ; same ref `docs/extensions.md`, `docs/tui.md`, `src/cli/args.ts`, `README.md`. Package integrity is recorded in the registry receipt; source compatibility is established, while installed package byte identity and native rendering remain separate acceptance checks.
