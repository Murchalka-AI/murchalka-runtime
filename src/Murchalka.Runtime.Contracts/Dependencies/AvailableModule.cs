using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Dependencies;

/// <summary>Describes a verified module visible to dependency resolution.</summary>
/// <param name="Manifest">The validated module manifest.</param>
/// <param name="BundleDigest">The authenticated bundle digest.</param>
/// <param name="IsActive">Whether the module is healthy and active.</param>
public sealed record AvailableModule(ModuleManifest Manifest, string BundleDigest, bool IsActive);
