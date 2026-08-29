# Phase 7 Client Extension operations

## Inspect

`GET /client/v1/catalog` returns the complete active snapshot. `GET /client/v1/catalog/events` emits revision hints over SSE. `GET /client/v1/artifacts/{sha256}` serves only an artifact owned by an active module; disabled artifacts return `404` immediately. These endpoints contain public verification material only and never accept the administration token.

## Activate and disable

Install a signed bundle through the normal `modules/inbox` flow. Publication occurs only after bundle verification, dependency resolution, process handshake and health gating. Use the authenticated `POST /v1/modules/{moduleId}/disable` endpoint for an emergency stop. Confirm that the catalog revision increases and the extension disappears before considering the stop complete. Re-enable through `POST /v1/modules/{moduleId}/enable`; the extension is republished only after a new health gate.

## Diagnose

If a shell keeps the prior revision, check its SSE connection and fetch the complete catalog manually. A digest, signature, target, accessibility, schema, WASM limit or payload failure is client-side fail-closed and must not be bypassed. Repair or roll back the module bundle. Never add a publisher key to a client directly: update Runtime trust through the established publisher-key procedure so every shell receives the same public projection.
