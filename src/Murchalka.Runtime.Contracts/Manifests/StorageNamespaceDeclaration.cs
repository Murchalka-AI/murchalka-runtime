using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Declares one module-owned storage namespace and its portability semantics.</summary>
/// <param name="Name">The stable namespace name local to the module.</param>
/// <param name="RequirementId">The storage requirement that owns the namespace.</param>
/// <param name="MigrationsPath">The bundle-relative migration manifest path.</param>
/// <param name="DataClassification">The highest data classification stored in the namespace.</param>
/// <param name="Exportable">Whether the namespace supports state export.</param>
/// <param name="PurgeMode">The explicit data purge policy.</param>
public sealed record StorageNamespaceDeclaration(
    string Name,
    string RequirementId,
    string MigrationsPath,
    DataClassification DataClassification,
    bool Exportable,
    StoragePurgeMode PurgeMode);
