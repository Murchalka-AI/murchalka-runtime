# Murchalka Runtime

Phase 1 implementation of the product-neutral Murchalka microkernel. The Runtime starts with no modules and accepts signed `.murchalka` bundles through `modules/inbox` without a process restart.

Implemented boundaries:

- stable-file inbox discovery and atomic staging;
- strict archive, manifest, lock, hash, artifact and ECDSA signature verification;
- publisher trust and default-deny permission grants;
- content-addressed immutable installation store;
- durable lifecycle state and hash-chained Root audit;
- out-of-process supervision over a local Unix-domain socket;
- deny-by-default macOS process sandbox (read-only bundle, writable module data, only the private gateway socket);
- authenticated Module Protocol handshake, health gate, drain and stop;
- manifest-authoritative capability registration and invocation routing;
- local HTTP diagnostics plus enable/disable operations.

Dependency/category resolution, scoped bindings, pipelines, events, provider state and migrations intentionally remain later phases. A bundle declaring required dependencies is retained in `PendingDependencies` and is never executed.

## Build and test

```sh
dotnet restore --locked-mode
dotnet test --no-restore
```

Protocol packages are resolved from GitHub Packages. Configure credentials for the `murchalka` source (for example through `NuGetPackageSourceCredentials_murchalka`) on a clean machine; no cross-repository project reference is used.

## CI and releases

Pull requests and pushes to `main` run locked restore, formatting verification, Release build, tests and coverage on Linux, Windows and macOS. A second job publishes the portable host and verifies that it starts with zero modules and reports a healthy state. CodeQL runs for pushes, pull requests and a weekly schedule; Dependabot checks NuGet and GitHub Actions dependencies weekly.

Pushing a tag in `vX.Y.Z` or `vX.Y.Z-prerelease` format runs the complete release gate, publishes the framework-dependent .NET 10 Runtime host, smoke-tests the exact published output and creates `.tar.gz` and `.zip` archives with SHA-256 checksums. The archives are attached to a generated GitHub Release. Runtime implementation projects are intentionally not published as public NuGet packages.

Release archives are immutable and include GitHub build-provenance attestations. Repository configuration, verification and rollback procedures are documented in [`docs/operations/release.md`](docs/operations/release.md).

```sh
git tag v0.1.0
git push origin v0.1.0
```

## Run

```sh
dotnet run --project src/Murchalka.Runtime.Host -- --root ./var
```

The control API listens on loopback only (default `http://127.0.0.1:5078`). Trust keys live in `configuration/trusted-publishers.json`; grants live in `configuration/grants`. Empty permission requests receive an implicit empty grant, while any non-empty request requires an explicit signed grant.
