# Murchalka Runtime

Phase 5 implementation of the product-neutral Murchalka microkernel. The Runtime starts with no modules and accepts signed `.murchalka` bundles through `modules/inbox` without a process restart.

Implemented boundaries:

- stable-file inbox discovery and atomic staging;
- strict archive, manifest, lock, hash, artifact and ECDSA signature verification;
- publisher trust and default-deny permission grants;
- content-addressed immutable installation store;
- durable lifecycle state and hash-chained Root audit;
- out-of-process supervision over a local Unix-domain socket;
- deny-by-default Linux Bubblewrap and macOS process sandboxes, with process modules kept fail-closed on Windows until an AppContainer launcher is available;
- authenticated Module Protocol handshake, health gate, drain and stop;
- manifest-authoritative capability registration and invocation routing;
- deterministic exact-module, capability, and category dependency resolution;
- SemVer, qualifier, scope, cardinality, conflict, and hard-cycle enforcement;
- revisioned scoped administrator bindings with optimistic concurrency and Root audit;
- live dependency endpoint updates and fail-closed dependency-loss reconciliation;
- generated Runtime composition locks that preserve exact module dependencies in a namespaced extension;
- manifest-authoritative dynamic pipeline definitions and handler contributions;
- deterministic DAG ordering, stage execution semantics, exactly-one bindings, and atomic graph snapshots;
- live pipeline and event contributor attachment/detachment during module lifecycle changes;
- schema-validated durable event outbox, inbox deduplication, partition ordering, bounded retry, quarantine, and replay;
- schema-validated revisioned module configuration with signed defaults, optimistic concurrency, live reload and restart policies;
- stable provider-owned data directories separated from ephemeral module instances;
- signed linear storage migrations routed through the resolved provider, with durable ledgers and reversible upgrade rollback;
- digest-verified state export/import that preserves module ownership and requires import while the consumer is inactive;
- provider-backed secret persistence, manifest/grant intersection, bounded leases and payload-free Root audit;
- authenticated reverse capability routing restricted to the current dependency snapshot;
- side-by-side module upgrade health gating, route switching, rollback retention and prior-bundle recovery;
- local HTTP diagnostics plus lifecycle, configuration, state portability and secret administration operations.

The first-party SQLite `storage.records@1` and local `secrets.provider@1` providers are delivered independently in `murchalka-storage-sqlite` and `murchalka-module-secrets-local`; neither is compiled into the Runtime. Product profile modules and remote or clustered providers remain later phases. Missing providers, ambiguous administrator selection, incompatible ranges, permission gaps, conflicts, cycles and irreversible failed upgrade migrations remain fail-closed.

## Build and test

```sh
dotnet restore
dotnet test --no-restore
```

Protocol packages are resolved from GitHub Packages. Configure credentials for the `murchalka` source (for example through `NuGetPackageSourceCredentials_murchalka`) on a clean machine; no cross-repository project reference is used.

Linux process modules require Bubblewrap. On Ubuntu hosts, `sudo bash scripts/prepare-linux-sandbox.sh` installs Bubblewrap, loads the restricted AppArmor user-namespace profile when required, and verifies the sandbox before Runtime tests are executed. The released Deployment profile sets `MURCHALKA_LINUX_NETWORK_ISOLATION=namespace-launcher`; modules without network permission are placed in an empty unprivileged network namespace by a minimal parent-mapping launcher before Bubblewrap starts, avoiding any requirement for container `NET_ADMIN` or privileged mode.

## CI and releases

Pull requests and pushes to `main` run locked restore, formatting verification, Release build, tests and coverage on Linux, Windows and macOS. Linux jobs first run a real Bubblewrap self-test with the restricted AppArmor user-namespace profile. A second job publishes the portable host and verifies that it starts with zero modules and reports a healthy state. CodeQL runs for pushes, pull requests and a weekly schedule; Dependabot checks NuGet and GitHub Actions dependencies weekly.

Pushing a tag in `vX.Y.Z` or `vX.Y.Z-prerelease` format runs the complete release gate, publishes the framework-dependent .NET 10 Runtime host, smoke-tests the exact published output and creates `.tar.gz` and `.zip` archives with SHA-256 checksums. The archives are attached to a generated GitHub Release. Runtime implementation projects are intentionally not published as public NuGet packages.

Release archives are immutable and include GitHub build-provenance attestations. Repository configuration, verification and rollback procedures are documented in [`docs/operations/release.md`](docs/operations/release.md).

```sh
git tag v0.1.0
git push origin v0.1.0
```

## Run

```sh
mkdir -p ./var/configuration
cp configuration/trusted-publishers.json ./var/configuration/trusted-publishers.json
openssl rand -base64 32 > ./admin-token
chmod 0600 ./admin-token
dotnet run --project src/Murchalka.Runtime.Host -- --root ./var --admin-token-file ./admin-token
```

The control API listens on loopback only (default `http://127.0.0.1:5078`), and every `/v1` request requires the bearer token loaded from `--admin-token-file`; `/health` remains unauthenticated for local supervision. Trust keys live in `configuration/trusted-publishers.json`; grants live in `configuration/grants`; bindings live in `configuration/murchalka.bindings.yaml`; module configuration lives in `configuration/modules`; migration ledgers and exports live under `modules/migrations` and `modules/exports`; provider-owned encrypted secret state lives under `module-data/dev.murchalka.secrets-local/state/vault`. Empty permission requests receive an implicit empty grant, while any non-empty request requires an explicit signed grant.

Configuration endpoints use optimistic revisions. Secret values are accepted only as Base64 and are never returned by the administration API. State imports accept only Runtime-generated export ids and require the consumer module to be inactive. Operational procedures are documented in [`docs/operations/phase4-runbook.md`](docs/operations/phase4-runbook.md).
