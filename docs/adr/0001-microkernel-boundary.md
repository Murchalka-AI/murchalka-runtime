# ADR-0001: Phase 1 microkernel boundary

Status: Accepted

## Decision

The Runtime implements only bundle discovery, Root Trust, immutable storage, module lifecycle/isolation, Module Protocol connectivity, health, audit and capability routing. It contains no product capability, product authorization rule, storage provider or known module list. Required dependencies are fail-closed until the Phase 2 resolver is available.

The process execution mode is the Phase 1 vertical slice. It uses a per-instance Unix-domain socket and an authenticated protocol session. Child processes receive a clean environment containing only runtime identity, socket and one-time proof material. Container, WASM, remote and in-process artifacts are not selected in this phase.

## Consequences

Runtime can boot empty and can activate or disable compatible signed process bundles without restart. Additional feature semantics remain independent modules. Other isolation modes can implement the same supervisor/gateway interfaces without changing the kernel.
