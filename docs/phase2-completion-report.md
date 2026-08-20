# Phase 2 completion report

## Outcome

The Runtime resolves exact module, exact capability, and category requirements without product knowledge or install-order selection. It enforces SemVer, qualifiers, scopes, cardinality, optional fallbacks, permissions, conflicts, hard cycles, explicit pending states, revisioned administrator bindings, live endpoint updates, dependency-loss impact, and generated composition locks.

## Repository and bundle

Repository: `murchalka-runtime`; solution: `Murchalka.Runtime.slnx`; Runtime version: `0.1.0`. Phase 2 adds the product-neutral `Murchalka.Runtime.Dependencies` and `Murchalka.Runtime.Bindings` projects without cross-repository project references.

## Manifest

The Runtime projection now preserves exact module requirements, required and optional capability/category requirements, declarative conditions, conflicts, provider qualifiers, and supported scopes from the canonical Phase 0 schema.

## Dependency resolution

Candidate selection is deterministic by version and stable identities. `admin` ambiguity and `scoped` requirements produce `PendingBinding`; missing providers produce `PendingDependencies`; missing invocation authority produces `PendingPermission`; version/qualifier mismatch produces `Incompatible`; conflicts and cycles produce `Conflict`. Optional requirements activate their named fallback.

## Contracts

Resolved dependencies are delivered as `DependencyEndpointsSnapshot` during startup and binding updates. Exact module resolutions and fallback names are preserved in the namespaced `dev.murchalka.resolver` extension of the schema-compatible generated lock.

## State and migrations

Binding state is deployment-owned and atomically revisioned. Provider loss drains required consumers before they enter a pending state. Migration-aware stateful rebinding remains deliberately fail-closed until the storage phase.

## Security

Binding documents are schema-validated, duplicate targets are rejected, updates use optimistic concurrency, and Root audit records both commits and applied routes. A capability requirement must also be present in the consumer's requested invocation permissions; the separately signed grant remains authoritative.

## Runtime lifecycle

Verified pending bundles are retained in the immutable store. Provider activation and binding commits reconcile pending modules without Runtime restart. Active consumers receive authenticated live binding updates, and generated locks are atomically refreshed.

## Verification

Analyzer-clean build, existing security/lifecycle E2E tests, exhaustive provider-permutation properties, multiple storage provider administrator selection, exact-cycle diagnostics, optional fallback behavior, binding schema/revision tests, and generated lock assertions.

## Remaining risks

- Stateful provider migration and rollback require the Phase 4 storage handshake.
- Profile bindings and arbitrary configuration predicate values require their later profile/configuration stores.
- Container, WASM, remote, and in-process providers need execution adapters that preserve the same resolver contracts.
