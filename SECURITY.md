# Security policy

The Runtime treats module bundles, manifests, schemas, permission grants, protocol messages and module processes as untrusted input. Signature, hash, compatibility, permission, sandbox, lifecycle and routing checks must fail closed before module code can become active.

Please report vulnerabilities privately to the repository maintainers. Do not open a public issue for a suspected vulnerability and do not include secrets, private keys, tokens, personal data or production bundle contents in a report.

Security fixes must include a regression test when a deterministic test is possible. A release containing a security fix must use a new immutable SemVer version; existing packages or release artifacts must never be replaced in place.
