namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes a runtime-targeted module artifact.</summary>
/// <param name="Id">The artifact identifier.</param>
/// <param name="Mode">The execution mode.</param>
/// <param name="OperatingSystems">The compatible operating systems.</param>
/// <param name="Architectures">The compatible processor architectures.</param>
/// <param name="EntryPoint">The bundle-relative entry point.</param>
/// <param name="Digest">The artifact SHA-256 digest.</param>
/// <param name="ProtocolVersion">The required Module Protocol major.</param>
public sealed record RuntimeArtifact(string Id, string Mode, IReadOnlySet<string> OperatingSystems, IReadOnlySet<string> Architectures, string EntryPoint, string Digest, int ProtocolVersion);
