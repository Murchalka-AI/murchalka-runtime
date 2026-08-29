# ADR-0009: Runtime-owned Client Extension catalog

Status: Accepted

## Decision

Runtime publishes one monotonically revisioned, atomic catalog derived only from active, fully verified module bundles. A catalog entry identifies its owning module, signed publisher key, supported targets, execution mode, accessible fallback, content digest, size and same-origin artifact URL. Runtime rechecks the manifest digest and extension envelope before publication; clients independently verify the artifact digest and detached publisher signature before activation.

The public `/client/v1` projection contains no credentials, secrets, configuration, database access or authority. It is loopback-only, read-only, CORS-enabled for local shells, redirect-free and protected by immutable content addresses. Module disable, failed activation, process exit, dependency loss, upgrade and rollback retract artifacts before publishing the next revision. SSE is only a revision hint; clients always fetch a complete snapshot and activate it atomically.

Client Actions never execute at this endpoint. They cross the authenticated realtime protocol, are bounded by the Client Runtime, resolved to a declared provider and validated again by that server module. WASM execution, CSP, rendering and persistent verified cache policy remain responsibilities of the product-agnostic Client Runtime package, not the server microkernel.

## Consequences

Already-built Web and Desktop shells can discover a newly activated Mini App without Runtime or shell feature changes. Corrupt artifacts cannot enter the catalog, unsigned artifacts cannot activate in a client, and catalog activation failure leaves the prior client snapshot intact. Clients may operate from previously verified immutable cache entries while offline, but disabling an extension removes its server artifact immediately and publishes a newer catalog revision.
