# Phase 1 completion report

## Outcome

The product-neutral Runtime boots with zero modules and implements the signed drop-in Hello vertical slice: stable inbox discovery, verification, install, authenticated process start, health-gated capability registration, invocation, disable, re-enable and restart reconciliation without rebuilding or restarting Runtime for module changes.

## Repository and bundle

Repository: `murchalka-runtime`; solution: `Murchalka.Runtime.slnx`; Runtime version: `0.1.0`. Bundle identity is the SHA-256 digest of the canonical signed file-hash set. Installed content is addressed by that digest and made read-only.

## Manifest and contracts

Canonical Phase 0 schemas validate manifest, lock, capability contracts and permission grants. Runtime selects exactly one compatible process artifact and verifies artifact/contract/request/response schema files. Declared capabilities are the only capabilities that can register; invocation payloads and successful results are validated against their versioned schemas and size limits.

## Dependency resolution

Phase 1 does not guess dependency providers. A module with required dependencies becomes `PendingDependencies` and remains unroutable until the Phase 2 resolver/binding implementation.

## Security

ECDSA P-256 publisher signatures, full payload hashing, publisher trust, separately signed permission grants, compatibility checks, archive traversal/duplication/size defenses, one-time HMAC process proof, clean child environment, manifest-authoritative registration and a hash-chained Root audit are enforced before routing. On macOS the process runs under a deny-by-default sandbox that restricts reads to its signed bundle, writes to module data, blocks process forks/outbound network, and permits only its private Unix gateway socket. The E2E fixture verifies that Root Trust configuration cannot be read by the module.

## Runtime lifecycle

Lifecycle state/revision is atomically persisted and audited. Disable unregisters routing before drain/stop and retains bundle/configuration/data. Re-enable creates a fresh authenticated instance. Runtime restart reconciles an active desired module from the immutable bundle. Corrupt bundles go to quarantine; untrusted and under-granted bundles remain staged in explicit pending states.

## Verification

Locked restore, Release build and nine tests pass with zero warnings. Coverage includes corruption, unknown publisher, partial-copy discovery, permission default-deny, unresolved dependency fail-closed, audit tamper/redaction, sandbox boundary, request schema rejection, drop/invoke/disable/re-enable and restart recovery.

## Remaining risks

- Equivalent mandatory OS sandbox backends for Linux and Windows need production hardening; the macOS backend is the enforced/tested Phase 1 target in this repository.
- Side-by-side upgrade, migration-aware rollback and provider rebinding belong to later phases; a different digest for an already installed module is quarantined rather than replaced in place.
- Dependency/category/cardinality resolution and scoped admin bindings intentionally begin in Phase 2.
- Container, WASM, remote and in-process execution adapters are not selected by the Phase 1 process supervisor.
