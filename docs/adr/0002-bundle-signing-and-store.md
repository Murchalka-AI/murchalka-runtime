# ADR-0002: Bundle identity, signing and immutable store

Status: Accepted

## Decision

`.murchalka` is a ZIP archive with normalized forward-slash paths. `manifest/file-hashes.json` lists SHA-256 hashes for all security-relevant entries except itself and `signature/`. The `module.lock.json` hash is calculated after replacing `module.bundleDigest` with 64 zeroes; this breaks the otherwise circular digest relationship. The bundle identity is SHA-256 of the canonical ordered file-hash set. The final lock must contain that identity.

`signature/signature.json` signs the canonical file-hash document with ECDSA P-256/SHA-256 and identifies a publisher/key pair in the local Root Trust store. Archive structure, duplicate/path traversal entries, size limits, canonical schemas, every listed hash, artifact/contract digests, module identity and signature are verified before extraction or execution.

Verified bundles are extracted once into `modules/installed/sha256/{digest}`. A temporary sibling directory is atomically renamed into place. Store contents are read-only; lifecycle markers reference their digest and module data is outside the store.

## Consequences

ZIP metadata and compression do not affect bundle identity, reproducible builders can generate identical identities, and publisher trust is an explicit local decision. A changed payload, lock or signature cannot be activated under an existing identity.
