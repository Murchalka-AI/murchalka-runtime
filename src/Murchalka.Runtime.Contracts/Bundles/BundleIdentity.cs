namespace Murchalka.Runtime.Contracts.Bundles;

/// <summary>Identifies authenticated bundle content and its signing key.</summary>
/// <param name="Digest">The canonical bundle SHA-256 digest.</param>
/// <param name="Publisher">The verified publisher identifier.</param>
/// <param name="KeyId">The trusted publisher key identifier.</param>
public sealed record BundleIdentity(string Digest, string Publisher, string KeyId);
