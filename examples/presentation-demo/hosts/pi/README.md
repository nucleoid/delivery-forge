# Pi package — native API source, harness-tested only

`forge-demo-extension.js` is an actual dependency-free Pi extension source targeting the APIs pinned for `@earendil-works/pi-coding-agent` 0.80.7 / `818d674…`: `registerCommand`, `setWidget`, `setStatus`, `custom`, `appendEntry`, `sendMessage`, `session_start`, `session_shutdown`, and active-branch `getBranch()` restore.

It has not been loaded into an installed Pi host. The contract tests use labelled test doubles and are not native certification. The persistent widget is a width-aware component factory; evidence and `/forge-demo usage` panels bound themselves to 18 rows, scroll with `j/k`, close with Escape, and use cache-only invalidation. Usage comes from the shared synthetic `forge.usage/0.1` projection, not Pi session statistics or transcript parsing. It creates no timers, processes, network traffic, model turns, or live authority. Non-TUI custom messages use `triggerTurn:false`.
