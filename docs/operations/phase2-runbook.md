# Phase 2 operations runbook

- **PendingDependencies:** install and activate a compatible required module/provider. The Runtime automatically retries pending consumers after provider activation.
- **PendingBinding:** inspect the deterministic candidates, then commit a schema-valid binding with `PUT /v1/bindings?expectedRevision=N`. Never choose by directory or install order.
- **PendingPermission:** ensure the consumer manifest requests the dependency capability/category and install a separately signed grant covering that exact request and bundle digest.
- **Incompatible:** compare the consumer range, provider version, qualifiers, scope, and target. Install a compatible provider or upgrade the consumer; do not weaken ranges in deployment configuration.
- **Conflict:** inspect the status reason. Resolve declared module conflicts or the complete hard-cycle path before retrying.
- **Binding revision conflict:** fetch `GET /v1/bindings`, merge the administrator's intended change into the latest revision, increment by exactly one, and retry.
- **Invalid binding file:** restore the last known schema-valid revision. Runtime resolution fails closed and never treats unknown fields as authority.
- **Provider loss:** routing is removed first. Required consumers drain into a pending state; optional consumers retain their named fallback. Re-enable the provider or commit a compatible binding.
- **Composition audit:** correlate `bindings.revised`, `bindings.applied`, `composition.locked`, and `module.transition` records. Generated locks are under `modules/locks/{module-id}.lock.json` and must not be edited manually.
- **Rollback:** restore the prior binding document as a new monotonic revision. The Runtime applies it live to healthy stateless consumers; stateful migration-aware rebinding belongs to the storage phase and must remain fail-closed until then.
