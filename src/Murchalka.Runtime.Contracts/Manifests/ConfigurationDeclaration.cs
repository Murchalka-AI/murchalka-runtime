namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes a module configuration contract and its change policy.</summary>
/// <param name="SchemaPath">The bundle-relative JSON Schema path.</param>
/// <param name="DefaultsPath">The optional bundle-relative defaults document path.</param>
/// <param name="RestartPolicy">The action required after a configuration change.</param>
public sealed record ConfigurationDeclaration(
    string SchemaPath,
    string? DefaultsPath,
    ConfigurationRestartPolicy RestartPolicy);
