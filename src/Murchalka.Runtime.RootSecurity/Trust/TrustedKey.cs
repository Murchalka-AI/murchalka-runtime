namespace Murchalka.Runtime.RootSecurity.Trust;

/// <summary>Describes a trusted public signing key.</summary>
/// <param name="KeyId">The key identifier.</param>
/// <param name="Algorithm">The signing algorithm identifier.</param>
/// <param name="PublicKeyPem">The PEM-encoded public key.</param>
public sealed record TrustedKey(string KeyId, string Algorithm, string PublicKeyPem);
