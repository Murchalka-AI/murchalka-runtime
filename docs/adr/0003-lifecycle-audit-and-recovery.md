# ADR-0003: Lifecycle, audit and crash recovery

Status: Accepted

## Decision

Every lifecycle transition is validated against a closed transition graph, persisted by atomic file replacement, then appended to a Root audit JSONL hash chain. Activation registers capabilities only after protocol authentication and a ready health result. Disable first removes routing, then drains and stops the process, and retains bundle/configuration/data.

On startup the kernel reloads installed records. Records that were in transient execution states are marked failed; active records are reconciled by starting fresh instances. Inbox processing is serialized per bundle identity and idempotent.

The Root audit stores identifiers, reason codes, hashes and timestamps, never module payloads or secrets.

## Consequences

The runtime can explain bundle verification, activation and disable decisions after restart. Process liveness never implies authorization or readiness, and a half-completed activation cannot leave a routable capability.
