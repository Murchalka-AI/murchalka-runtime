# ADR-0005: Scoped administrative binding resolution

Status: Accepted

## Decision

The deployment-owned `configuration/murchalka.bindings.yaml` document is schema-validated on every read. It is never supplied by a bundle. Replacements use optimistic concurrency: the caller supplies the revision it observed, and the new document must advance it by exactly one. Writes use a temporary sibling followed by an atomic replace. Duplicate binding ids and duplicate consumer/requirement/scope targets are rejected.

Scope contexts are explicit and ordered from most specific to global. Parent lookup occurs only when `missingScopedBinding: inheritParent` permits it. The Runtime activation context includes the consuming-module scope and global scope. Provider references use a stable logical instance name; process instance ids remain ephemeral authenticated routing identities.

After a binding commit, active consumers receive an immutable dependency snapshot through the authenticated Module Protocol and their generated composition locks are atomically replaced. Pending consumers are reconciled immediately. Provider loss removes routing first, then consumers with unsatisfied required dependencies are drained and moved to their precise pending state. Every commit and applied route revision is written to Root audit.

## Consequences

Administrators can reproduce a composition using the binding revision and generated lock. Direct external file edits are still validated fail-closed when read, but the loopback API is the supported mutation path because it provides optimistic concurrency and audit. Profile-derived bindings and migration-aware stateful switches remain later phases.
