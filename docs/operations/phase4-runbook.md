# Phase 4 operations runbook

- **Configuration rejected:** validate the administrator values against the schema in the installed immutable bundle. Do not edit stored snapshots or signed defaults. Retry `PUT /v1/modules/{module}/configuration?expectedRevision=N` with the current revision.
- **Reload rejected:** the Runtime drains and restarts the module using the committed snapshot. If restart fails, inspect module status and Root audit; fix configuration with a newer revision or restore the prior values as another monotonic revision.
- **Target restart required:** `restartTarget` configuration is durable but intentionally not sent to the running process. Schedule a Runtime/target restart; do not force a live reload.
- **Migration checksum or chain failure:** compare `migrations.yaml`, the immutable artifact and the current ledger under `modules/migrations`. Publish a corrected new bundle; never edit immutable content or the ledger manually.
- **Migration provider unavailable:** restore the declared storage binding/provider. The consumer remains inactive until its required provider is healthy and the migration succeeds.
- **Upgrade failure:** the candidate is stopped. Reversible migrations run down in reverse and the verified prior bundle is reactivated. If any migration is irreversible, the module remains failed; apply the declared forward-fix recovery procedure before activation.
- **State export:** call `POST /v1/modules/{module}/state/{namespace}/export`. Preserve both `.state` and `.state.json` under `modules/exports`; the metadata digest authenticates the content.
- **State import:** disable the consumer, verify the target provider binding, then call `POST /v1/modules/{module}/state/import/{exportId}`. Imports from arbitrary paths or another module are rejected.
- **Secret update:** send Base64 bytes to `PUT /v1/secrets/{name}?expectedRevision=N`. The response contains revision metadata only. Rotate dependent services, then update the secret as a new revision.
- **Secret lease denied:** confirm the exact name exists in both manifest `permissions.secrets` and the effective signed grant, the grant is current, and the request deadline/purpose are valid. Never broaden the grant to `*` as a diagnostic shortcut.
- **Secret master key loss:** restore `configuration/secrets/master.key` and encrypted records from the same backup generation. A mismatched key makes records undecryptable by design.
- **Backup:** snapshot bindings, grants, module configuration, encrypted secrets plus master key, migration ledgers, exports, stable `module-data`, immutable bundle digests and Root audit together.
