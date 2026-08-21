# ADR-0006: Dynamic pipelines and durable local events

Status: Accepted

## Decision

The Runtime composes pipeline definitions and handler contributions only from verified, healthy module instances. A module declares definition document paths in the `dev.murchalka.pipelines` manifest extension and declares handlers through canonical `contributes.pipelines` entries. Every rebuild creates a complete immutable graph snapshot and publishes it atomically. Ordering constraints form a DAG; missing optional ordering targets are ignored, duplicate handler ids and cycles reject the contributing activation, and priority never overrides dependency edges.

Pipeline definitions own ordered stages, input/output schemas, deadlines, cancellation, and checkpointing declarations. `exactlyOne` stages use the existing fail-closed binding document: the definition owner is the binding consumer, the stage id is the requirement id, and the pipeline id identifies the provider capability. Multiple handlers without that binding make the pipeline unavailable instead of selecting by discovery order. Sequential, parallel-merge, first-successful, exactly-one, fan-out, and reduce modes share deadline propagation and schema validation.

The local Event Fabric accepts publications and subscriptions declared in the verified manifest. It validates payloads against immutable bundle schemas and gives the Runtime authority over `publishedAt` and `payloadSchema`. Non-public events additionally require an effective grant whose `data.read` or `data.write` set names the topic, `event:{topic}`, `event:*`, the classification, or `*`.

Publishing atomically appends an immutable envelope plus its publication-time recipients to a filesystem outbox. Delivery is at least once. A durable receipt keyed by event id, consumer module, and stable handler id provides inbox deduplication across process restarts. Earlier pending events block later events only within the same declared partition. Failures use bounded exponential retry; exhausted, schema-incompatible, or unauthorized deliveries enter a durable quarantine that can be explicitly replayed. Audit records contain identifiers and reason codes, never payload content.

Disable, crash, and shutdown remove pipeline/event routes before process stop. Re-enable publishes a new graph revision and resumes still-pending deliveries for the same stable handler identity. Required capability loss continues to use Phase 2 draining and pending lifecycle states; optional dependency endpoint changes are delivered live.

## Consequences

Relationship-like, memory-like, and policy-like modules can attach to or detach from product-defined behavior without Runtime restart or product knowledge. Pipeline readers never observe a partially rebuilt graph, event redelivery is safe for conforming idempotent handlers, and ambiguous exactly-one composition remains an administrative decision.

Atomicity between a producer's domain state and the Runtime filesystem outbox cannot be guaranteed until Phase 4 storage providers expose a shared transactional outbox contract. Phase 3 guarantees an atomic durable append after the producer supplies a committed event id; producer retry plus durable consumer deduplication closes the delivery gap without claiming cross-store atomicity.
