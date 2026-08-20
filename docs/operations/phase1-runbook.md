# Phase 1 operations runbook

- **Stuck in staging:** inspect module status and Root audit; fix the producer flow and re-drop an atomically renamed bundle. `.partial` and `.tmp` are intentionally ignored.
- **AwaitingTrust:** add the publisher/key public PEM to `configuration/trusted-publishers.json`, then call enable or re-drop the bundle.
- **PendingPermission:** create a separately signed grant bound to the exact bundle digest. Grants exceeding the manifest request are rejected.
- **PendingDependencies:** the module needs Phase 2 dependency/binding resolution and must remain inactive.
- **Signature/corruption:** the original file is moved to quarantine with a sidecar reason. Never copy it into installed manually.
- **Crash or failed health:** inspect redacted stderr tail and audit reason. Re-enable after correction; repeated failures remain unroutable.
- **Disable:** call `POST /v1/modules/{id}/disable`; this drains and stops but retains data and bundle.
- **Recovery:** restart Runtime. Installed records are reconciled; transient states become Failed and previously active modules are started again.
