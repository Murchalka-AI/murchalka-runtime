# ADR-0004: Dependency cardinality and deterministic selection

Status: Accepted

## Decision

Dependency resolution is a pure product-neutral operation over validated manifests, the healthy provider snapshot, the verified module catalog, administrative bindings, scope context, and declarative configuration values. It supports exact module, exact capability, and category requirements; SemVer ranges; typed qualifier equality; all manifest cardinalities; optional named fallbacks; and declarative conditions.

Provider input order is never meaningful. Candidates are ordered by capability version descending, module id ordinal, module version descending, logical instance ordinal, and authenticated runtime instance ordinal. `admin` fails with `PendingBinding` when more than one compatible provider remains. `scoped` always requires a binding. `automatic` and unbound `preferred` use the deterministic order. `consumerPolicy`, plural cardinalities, and `allMatching` preserve the deterministically ordered authorized candidate set.

Hard exact-module and resolvable capability cycles fail with `Conflict` and include the complete cycle path. Declared two-way module conflicts also fail before activation. Missing, incompatible, ambiguous, under-permitted, and conflicting graphs map to distinct durable lifecycle states and never partially start a module.

## Consequences

Install order and filename order cannot change composition. Resolver behavior can be property-tested without processes or files. Stateful or side-effect providers still default to administrator selection through the manifest schema; migration-aware stateful rebinding remains a later storage phase.
