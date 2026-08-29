namespace Murchalka.Runtime.Contracts.ClientExtensions;

/// <summary>Describes one immutable artifact in the active client extension catalog.</summary>
/// <param name="ExtensionId">The globally unique extension identifier.</param>
/// <param name="ExtensionVersion">The extension version.</param>
/// <param name="ModuleId">The owning module identifier.</param>
/// <param name="ModuleVersion">The owning module version.</param>
/// <param name="ArtifactId">The manifest-local artifact identifier.</param>
/// <param name="ArtifactDigest">The content-addressed SHA-256 digest.</param>
/// <param name="ArtifactBytes">The immutable artifact length.</param>
/// <param name="ArtifactUrl">The Runtime-relative artifact URL.</param>
/// <param name="Mode">The declarative or WASM mode.</param>
/// <param name="Targets">The supported client targets.</param>
/// <param name="Publisher">The verified bundle publisher.</param>
/// <param name="KeyId">The verified publisher key identifier.</param>
/// <param name="FallbackComponent">The standard fallback component.</param>
public sealed record ClientExtensionCatalogEntry(
    string ExtensionId,
    string ExtensionVersion,
    string ModuleId,
    string ModuleVersion,
    string ArtifactId,
    string ArtifactDigest,
    long ArtifactBytes,
    string ArtifactUrl,
    string Mode,
    IReadOnlyList<string> Targets,
    string Publisher,
    string KeyId,
    string FallbackComponent);
