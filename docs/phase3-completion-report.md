# Phase 3 completion report

## Outcome

The Runtime now composes versioned product-neutral pipelines and durable local events from verified module declarations. Definitions, contributions, subscriptions, and publications attach after the module health gate and detach before drain/stop without Runtime restart.

## Repository and solution

Repository: `murchalka-runtime`; solution: `Murchalka.Runtime.slnx`; Runtime version: `0.1.0`. Phase 3 adds the `Murchalka.Runtime.Pipelines` and `Murchalka.Runtime.Events` projects without cross-repository project references.

## Pipeline graph

Pipeline definitions are explicit signed bundle documents referenced through `extensions.dev.murchalka.pipelines.definitions`. Input/output schemas, stages, deadlines, cancellation, and checkpointing semantics are validated before activation. Contributions come from the canonical manifest schema. Every registration, removal, or binding revision rebuilds a complete immutable graph and swaps one monotonic snapshot.

Handler ordering is a deterministic DAG. Contradictory edges reject activation and preserve the prior graph. Missing optional ordering targets do not block composition. All six specified stage modes are implemented. Exactly-one ambiguity is fail-closed and uses the Phase 2 binding store rather than install order.

## Event fabric

Publications and subscriptions are manifest-authoritative. Payload schemas and digests come from immutable bundle content. Publication appends a Runtime-stamped event envelope and its recipient set atomically to the durable outbox. Delivery is at least once, ordered per partition, deadline bounded, and deduplicated durably by event id plus module/handler identity.

Transient failures use bounded exponential retry. Schema incompatibility, authorization denial, and exhausted attempts create per-recipient quarantine records with replay identifiers. Root audit records publication, acknowledgement, retry, quarantine, and replay without payload content.

## Dependency impact and hot composition

Disable, crash, and shutdown remove capability, pipeline, and event routing before process termination. A newly active provider now also triggers live reconciliation of active optional consumers. Required dependency loss retains the Phase 2 drain-to-pending behavior. Relationship-like optional pipeline and event contributors attach and detach against immutable snapshots without changing the Runtime or defining module.

## Security and failure semantics

Contribution documents are covered by bundle hashes and verified before artifact execution. Runtime resolves every referenced path inside immutable bundle content. Non-public events require explicit effective data grants. Pipeline and event calls propagate deadlines and cancellation; graph conflicts never publish partial state; delivery failures never acknowledge inbox receipts.

## Verification

Tests cover live relationship-style attach/detach, graph revision changes, deterministic ordering, cycle rollback, exactly-one pending binding and selection, event schema validation, durable acknowledgement deduplication, detach/reattach delivery, quarantine, repair, and replay. Existing Phase 1–2 lifecycle, security, resolver, binding, and end-to-end tests remain part of the solution gate.

## Remaining risk

Atomic producer state plus outbox commit requires the Phase 4 storage provider transaction contract. Phase 3 therefore requires producers to persist a stable event id and retry the Runtime append; the Runtime provides idempotent durable consumer delivery but does not claim cross-store transactions.
