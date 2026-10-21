# Forge cockpit demo mod

Session-local, synthetic, read-only Claude Mod. It uses only bundled fixtures and the shared presentation projector. It does not observe production sessions, call a model, submit prompts, invoke tools, start processes, access the network, or alter permissions.

After static validation, load only for an isolated session:

```text
claude --plugin-dir <this-directory>
/forge-cockpit-demo
```

Arguments are bounded enums: `open`, `view overview|evidence|usage|activity`, `scenario <bundled-name>`, `reset`, `close`, and `help`.
