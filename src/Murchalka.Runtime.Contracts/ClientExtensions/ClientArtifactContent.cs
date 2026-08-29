namespace Murchalka.Runtime.Contracts.ClientExtensions;

/// <summary>Contains one verified immutable client artifact.</summary>
/// <param name="Digest">The canonical SHA-256 digest.</param>
/// <param name="Bytes">The bounded artifact bytes.</param>
public sealed record ClientArtifactContent(string Digest, ReadOnlyMemory<byte> Bytes);
