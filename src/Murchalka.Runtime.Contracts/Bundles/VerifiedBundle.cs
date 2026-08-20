using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Bundles;

/// <summary>Contains bundle metadata authenticated by Root Trust.</summary>
/// <param name="StagedPath">The staged archive path.</param>
/// <param name="Identity">The authenticated bundle identity.</param>
/// <param name="ArchiveDigest">The raw archive digest used for TOCTOU detection.</param>
/// <param name="Manifest">The validated manifest.</param>
/// <param name="FileHashes">The signed payload hashes.</param>
/// <param name="VerifiedAt">The verification timestamp.</param>
public sealed record VerifiedBundle(string StagedPath, BundleIdentity Identity, string ArchiveDigest, ModuleManifest Manifest, IReadOnlyDictionary<string, string> FileHashes, DateTimeOffset VerifiedAt);
