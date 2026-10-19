# Pi package — native API source, harness-tested only

`forge-demo-extension.js` is an actual dependency-free Pi extension source targeting the APIs pinned for laptop `@earendil-works/pi-coding-agent` 0.80.7 / `818d674…`: `registerCommand`, `setWidget`, `setStatus`, `custom`, `appendEntry`, `sendMessage`, `session_start`, `session_shutdown`, and active-branch `getBranch()` restore. The same surface was also inspected in later 1.3.0 research.

It has not been loaded into an installed Pi host. The contract tests use labelled test doubles and are not native certification. The persistent widget is a width-aware component factory; the detail panel visibly marks selection, bounds itself to 18 rows, scrolls detail content with `j/k`, and uses cache-only invalidation. It creates no timers, processes, network traffic, model turns, or live authority. Its non-TUI custom message is synthetic fixture text and uses `triggerTurn:false`.
