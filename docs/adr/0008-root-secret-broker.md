# ADR-0008: Root secret broker and local encrypted store

Status: Accepted

## Decision

Secrets are never placed in configuration snapshots, process arguments, ordinary environment variables, logs or audit details. The Root secret broker is the only Runtime path that releases secret material to a module process.

The Phase 4 local backend encrypts each secret revision with AES-256-GCM. Secret name is authenticated additional data, record filenames are SHA-256 name digests, writes are atomic and files use owner-only permissions where supported. The installation-scoped master key is generated once with cryptographic randomness and stored separately with owner-only permissions. The storage interface is replaceable by later external `secrets.provider` adapters without changing gateway or policy semantics.

A lease request arrives over an authenticated Module Protocol session and contains a name, purpose, operation id and deadline. The broker intersects the signed manifest request with the effective signed grant, rejects expired grants and missing secrets, and caps lease lifetime at five minutes, the request deadline and the grant expiry. Audit contains module, name, purpose, lease id, revision and expiry but never value bytes. Decrypted byte buffers are cleared after response construction.

The loopback administration API accepts Base64 secret bytes and returns only revision metadata. It never exposes a read endpoint. Root secret enforcement remains non-disableable even when a later provider supplies encrypted storage.

## Consequences

Modules receive only explicitly declared and granted secrets, for a bounded purpose and lifetime, without broad filesystem or environment access. Local self-hosted installations work without an external vault. Hardware-backed keys, provider rotation workflows and clustered secret backends can replace the storage interface while retaining the same Root broker checks.
