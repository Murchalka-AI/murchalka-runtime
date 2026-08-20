# Runtime release operations

## Repository configuration

The `murchalka-runtime` repository must have GitHub Actions enabled. The repository `GITHUB_TOKEN` needs read access to the public `Murchalka.ModuleProtocol.*` packages. If package inheritance is restricted by organization policy, connect the Runtime repository to both Protocol packages under package settings.

Protect `main` and require the three `Build and test` matrix checks, `Verify distributable Runtime`, and `Analyze C#`. Require pull requests and CODEOWNERS review, prevent force pushes and protect release tags matching `v*`. The CODEOWNERS file currently assigns `@schev4enko3`; replace that handle before enabling the rule if the repository maintainer uses a different GitHub login.

## Continuous integration

Pull requests and pushes to `main` perform locked dependency restore, formatting and analyzer verification, Release build, tests with coverage on Linux, Windows and macOS, and a publish smoke test. Test results, coverage and the portable Runtime output are retained as workflow artifacts for 14 days.

CodeQL runs on pull requests, pushes to `main`, manual dispatch and its weekly schedule. Dependabot checks NuGet and GitHub Actions dependencies weekly.

## Creating a release

Create a new immutable SemVer tag from an already validated commit:

```sh
git tag v0.1.0
git push origin v0.1.0
```

The release workflow validates the tag, restores locked dependencies, builds, tests, publishes and smoke-tests the exact Runtime output. It creates portable `.tar.gz` and `.zip` archives, `SHA256SUMS`, a GitHub build-provenance attestation and generated release notes.

Existing tags and release assets are never overwritten. A correction requires a new SemVer version.

## Verification

Verify the downloaded checksum before extraction:

```sh
sha256sum --check SHA256SUMS
```

When the GitHub CLI is available, verify the build provenance against this repository:

```sh
gh attestation verify murchalka-runtime-0.1.0.tar.gz --repo Murchalka-AI/murchalka-runtime
```

The archives are framework-dependent and require the .NET 10 ASP.NET Core Runtime. Start the host on an explicit loopback URL and provide a dedicated data root.

## Failure and rollback

A failed workflow does not authorize replacing an existing release. Fix the source or workflow on `main`, rerun CI and publish a new version. Runtime module rollback remains separate from Runtime binary rollback; retain the previous verified Runtime archive and its checksum until the new release passes operational validation.
