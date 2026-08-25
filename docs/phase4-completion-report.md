# Phase 4 completion report

## Outcome

The Runtime now provides a working product-neutral state/configuration/secrets vertical slice. Module state survives process and Runtime restart, signed migrations execute through selected providers, configuration reloads without rebuilding Runtime, and secrets are persisted through an independent provider then leased through Root policy rather than product-module configuration or environment injection.

## Repository and bundle

Runtime repository and solution: `murchalka-runtime/Murchalka.Runtime.slnx`, Runtime version `0.1.0`. First-party storage provider repository and solution: `murchalka-storage-sqlite/Murchalka.StorageSqlite.slnx`, module id `dev.murchalka.storage-sqlite`, version `0.1.0`. The provider has no cross-repository project references.

## Manifest

SQLite provides `storage.sqlite.records@1.0.0` in category `storage.records`, with local/SQLite/transaction/export qualifiers and no requested host permissions. Consumer manifests may declare signed configuration, storage namespaces and side-by-side upgrade policy using the canonical Phase 0 schema.

## Dependency resolution

Storage migrations, export/import and ordinary module dependency calls use the Phase 2 resolved provider instance and authorization reference. Reverse gateway requests fail closed when consumer identity or any endpoint field differs from the active dependency snapshot. Ambiguity remains administrator-bound.

## Contracts

Phase 4 adds Runtime contracts for validated configuration snapshots, storage namespaces, migration sets, exports, purge and upgrade policies, secret storage and leases. SQLite publishes versioned request/response schemas and passes the SDK repository conformance harness.

## State and migrations

Stable provider state is separate from ephemeral instances. SQLite physically isolates consumer module plus namespace, implements optimistic records, bounded scans, WAL durability, atomic idempotency receipts, transactional SQL migrations and digest/integrity-checked export/import. The Runtime persists a trusted per-namespace migration ledger and retains data on disable/removal.

## Security

Migration/configuration artifacts are resolved inside verified immutable bundle content. Namespace identity includes authenticated consumer identity. Local secrets use AES-256-GCM and owner-only key/record permissions. Lease authorization intersects manifest and signed grant and Root audit excludes values. Import accepts only Runtime-generated export ids while the consumer is inactive.

## Runtime lifecycle

Candidate upgrades authenticate and pass readiness side-by-side, drain the prior process, migrate, attach routes and activate before the durable bundle pointer changes. Reversible failures run down migrations before prior activation. Rollback content is retained for the declared window.

## Node and client artifacts

Phase 4 has no Node or client artifact. The provider manifest targets Runtime process execution. Node storage and client configuration remain target-specific later phases.

## Verification

Runtime tests cover default merging/schema/revision behavior, immutable configuration, encryption at rest, grant intersection, migration checksum/routing/deduplication and all existing Phase 1–3 scenarios. SQLite tests cover physical namespace isolation, restart durability, atomic idempotency, transactional migration and export/import restoration. The provider passes Phase 0 manifest/capability conformance, and a signed provider bundle passes the Runtime bundle verifier.

## Remaining risks

State export is inline and limited to the capability payload maximum; large streaming snapshots require an additive contract. The local secret backend is single-installation; clustered and hardware-backed providers remain separate future adapters. Irreversible migration failure requires its declared forward-fix runbook and intentionally blocks automatic old-code activation.
