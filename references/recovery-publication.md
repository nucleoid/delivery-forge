# Publication and uncertain-result recovery

Issue #10 RED checkpoint: public models and fail-closed stubs only. Publication behavior is not yet implemented or validated. No live GitHub operation is configured or authorized.

The parent owns publication. A worker's mode, validated receipt bytes, journal import or caller observation never constructs production authority. The run token has an opaque issuer, immutable ceiling/revision, exact patch inputs and independently pinned review receiver. A later explicit higher grant creates a new reconciled revision referencing prior evidence and repeats current head/policy checks; it cannot edit the old ceiling. Resume does not escalate.

Before any external action, the planned implementation must serialize repository/ref operations, read fresh remote ref/PR head/check/protection facts, then refresh local head/tree/full patch/artifacts/ownership/stop state and current accepted review. Required policy gates must have allowed exact-bound outcomes. Merge requires both independently issued and hosted review and protected normal-merge checks. No release, deployment, squash, rebase, auto-merge or bypass operation is in the host interface.

Each intent binds key, issuer-owned run/revision, action, repository/refs and base/head/tree. Record the send intent before invoking the host. After an uncertain response, inspect the remote result; an absent or ambiguous result remains unresolved and must not trigger automatic resend. Repeated keys cannot silently bind another revision/action. Readback-confirmed results produce append-only existing-v1 publication receipts. Receipt import is evidence only and cannot restore ephemeral authority after restart.

Fixture ports and their call ledger perform no network/operating-system publication. Fixture outcomes remain marked Fixture and cannot activate the disabled GitHub adapter. Real adapter capability, live ownership/source facts and explicit disposable-target authorization are separately required; production acceptance remains INCOMPLETE without them.

GitHub does not enforce external fencing tokens. Immediate checks reduce stale actions but leave a check-to-action race; document it in results. Rollback disables mutation while retaining receipts and reconciling uncertain remote state. Never store credentials in plans/results or change protected contract schemas to make fixture tests pass.
