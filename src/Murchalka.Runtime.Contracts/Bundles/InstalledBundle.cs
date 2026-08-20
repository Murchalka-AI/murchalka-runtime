using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Bundles;

/// <summary>Describes a bundle installed in the immutable store.</summary>
/// <param name="Digest">The canonical bundle digest.</param>
/// <param name="BundlePath">The preserved archive path.</param>
/// <param name="ContentPath">The extracted immutable content path.</param>
/// <param name="Manifest">The validated manifest.</param>
/// <param name="InstalledAt">The installation timestamp.</param>
public sealed record InstalledBundle(string Digest, string BundlePath, string ContentPath, ModuleManifest Manifest, DateTimeOffset InstalledAt);
