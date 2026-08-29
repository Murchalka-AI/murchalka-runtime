namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes a versioned UI component contributed by a client artifact.</summary>
/// <param name="Id">The globally unique component identifier.</param>
/// <param name="Version">The component contract major version.</param>
/// <param name="ArtifactId">The owning client artifact identifier.</param>
/// <param name="SchemaPath">The bundle-relative properties schema.</param>
public sealed record UiComponentContribution(string Id, int Version, string ArtifactId, string SchemaPath);
