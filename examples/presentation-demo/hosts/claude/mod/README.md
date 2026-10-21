# Forge cockpit demo mod

Session-local, synthetic, read-only Claude Mod. It uses only bundled fixtures and the shared presentation projector. It does not observe production sessions, call a model, submit prompts, invoke tools, start processes, access the network, or alter permissions.

After static validation, load only for an isolated session:

```text
claude --plugin-dir <this-directory>
/forge-cockpit-demo
```

The terminal command is deliberately **no-argument only**. Choose the bundled fixture and the Overview, Evidence, Usage, or Activity view with the pane's native `Select` and `Button` controls. The pane requests 30 rows and 88 columns; the host and user-resized layout remain authoritative, and the tree reflows below 40 body columns.

Do not type `/forge-cockpit-demo ...` with trailing arguments. Claude Code 2.1.306 did not reliably route that grammar as an immediate command in a real terminal and may treat the entire line as prompt text. If an argument-bearing event does reach this Mod, it fails closed without opening or changing the fixture. The official test kit proves the handler behavior, but is not evidence that every terminal build routes unsupported grammar safely.
