# Examples

Use absolute operator-selected paths in actual commands; shell quoting follows the host shell.

```text
delivery-forge --help
delivery-forge --version
delivery-forge capabilities --profile local --json
delivery-forge plan --repo ABSOLUTE_REPOSITORY_ROOT --intent-file ABSOLUTE_INTENT --config ABSOLUTE_CONFIG --plan-out ABSOLUTE_NEW_BUNDLE
delivery-forge implement --plan ABSOLUTE_BUNDLE --config ABSOLUTE_CONFIG --dry-run --json
delivery-forge resume --run-dir ABSOLUTE_RUN --config ABSOLUTE_CONFIG --ceiling implement --dry-run
delivery-forge pr --run-dir ABSOLUTE_RUN --config ABSOLUTE_CONFIG --dry-run
delivery-forge merge --run-dir ABSOLUTE_RUN --config ABSOLUTE_CONFIG --dry-run
```

These are syntax examples, not performed operations. An embedding must supply actual host/publication owner objects for effects. Missing prerequisites return INCOMPLETE/BLOCKED with retained evidence; they are not silently replaced by fixtures. `--replay-out` requests output only and is never an execution switch.
