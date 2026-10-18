# Removal and reinstall

Use the independently retained InstallationReceipt. Verify every original payload hash and the complete prefix topology before deleting anything. Changed/missing/foreign files or directories refuse removal, preserving user data.

BeginRemoval creates a distinct new sibling recovery directory with a pinned immutable plan. ContinueRemoval validates that exact plan and every remaining original before effects. Each deletion has separate CreateNew entered and returned records. A missing actual returned record is UNKNOWN and refuses replay, even if a path is now absent. A failed post-check cannot refund the deadline or turn absence into a known return. A completed step is not retried. Journal files remain as ordinary evidence after success; they are not a private runtime capability.

Uninstall wraps these APIs but retains partial journal/prefix state on failure. Reinstall requires the same archive identity, completes verified removal, installs into the now-new prefix and checks the same payload digest with a new nonce. No recursive deletion or adoption of an existing prefix is used. External concurrent hostile namespace changes are outside this cooperative profile; reparse and known drift checks remain mandatory.

Actual interrupted-install/removal/reinstall and both-OS installed golden proofs are still mandatory. Manual reconciliation of an entered-only removal needs original failure evidence; an automatic retry is not supported.
