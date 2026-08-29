namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes one signed client extension artifact.</summary>
/// <param name="Id">The manifest-local artifact identifier.</param>
/// <param name="ExtensionId">The globally unique extension identifier.</param>
/// <param name="ExtensionVersion">The extension contract version.</param>
/// <param name="Targets">The supported Client Runtime targets.</param>
/// <param name="Mode">The declarative or WASM execution mode.</param>
/// <param name="EntryPoint">The bundle-relative signed extension envelope.</param>
/// <param name="Digest">The signed artifact digest.</param>
/// <param name="FallbackComponent">The standard fallback component identifier.</param>
public sealed record ClientArtifact(
    string Id,
    string ExtensionId,
    string ExtensionVersion,
    IReadOnlySet<ClientTarget> Targets,
    string Mode,
    string EntryPoint,
    string Digest,
    string FallbackComponent);
