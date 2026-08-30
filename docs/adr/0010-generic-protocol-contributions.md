# ADR-0010: Generic protocol contributions

Status: Accepted

## Decision

Runtime validates and catalogs protocol contributions as generic metadata. It never parses MCP, A2A, or any future external protocol. Each contribution must point to a capability declared by the same verified module and must define a unique route namespace, supported transports and authentication, streaming shape, and bounded limits.

External listeners belong to the independently installable Protocol Gateway module. The gateway receives only dependency endpoints granted by Runtime and can therefore dispatch only to explicitly resolved protocol handlers. Disabling or revoking a module removes its capability endpoint during ordinary dependency reconciliation.

## Consequences

The loopback administrative API can display active protocol contributions without introducing protocol-specific product logic. Route collision resolution is administrative and deterministic. Protocol modules can be installed, upgraded, rolled back, and removed without a Runtime rebuild.
