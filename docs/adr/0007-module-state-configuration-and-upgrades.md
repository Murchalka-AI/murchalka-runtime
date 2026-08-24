# ADR-0007: Module-owned state, configuration and side-by-side upgrades

Status: Accepted

## Decision

The Runtime coordinates state but never implements a product or storage category. A signed module manifest may declare configuration and module-owned storage namespaces. Configuration schemas, defaults, migration manifests and migration artifacts are resolved only inside immutable verified bundle content.

Configuration is stored as administrator overrides plus a monotonic revision. The effective snapshot is a recursive merge of signed defaults and overrides and must satisfy the signed JSON Schema before persistence or activation. `reload` is delivered through Module Protocol; a rejected or broken reload restarts the affected module so persisted and effective state cannot diverge. `restartModule` performs an explicit drain/restart. `restartTarget` remains persisted and pending until target restart. `immutable` may only be initialized while inactive.

Process supervision gives every module two directories: an instance-scoped temporary working directory and a stable provider-owned state directory. Only these and immutable bundle content are visible to the sandbox. Disable, restart and ordinary bundle removal do not delete the stable directory.

Every storage namespace names one required capability requirement and one migration manifest. The Runtime verifies ownership, provider category, a deterministic linear version chain, artifact containment and SHA-256 checksums before invoking the resolved active provider. Each migration carries a stable idempotency key. A trusted local ledger advances only after provider success. Export and import use the same binding, include content digests and schema versions, and import only Runtime-owned exports while the consumer is inactive.

Upgrades require a newer version, the same publisher and an explicit `sideBySide` policy. The candidate completes authenticated startup and readiness before the prior route is removed. The prior instance drains before migrations. The new lifecycle record and routes commit only after migration, pipeline/event registration and activation succeed. Prior immutable content is retained with an expiry reference. If candidate activation fails, reversible down artifacts run in reverse before the prior bundle is restarted. An irreversible migration failure is fail-closed; the Runtime does not start old code against an unknown newer schema.

Modules invoke storage and other dependencies over the authenticated gateway. The Runtime accepts reverse capability calls only when consumer identity matches the session and provider instance, capability, version and authorization reference match the current dependency snapshot. Install order and caller-supplied namespace names never authorize cross-module access.

## Consequences

Module-owned state survives disable, Runtime restart and compatible upgrades without granting modules direct Runtime database access. Configuration conditions now resolve against the effective validated snapshot. Stateful rebinding and portability have explicit auditable operations.

The Phase 4 export envelope is bounded by the request/response capability payload limit. Large snapshot streaming remains a later additive protocol contract. Automatically rolling back an irreversible migration is intentionally unsupported.
