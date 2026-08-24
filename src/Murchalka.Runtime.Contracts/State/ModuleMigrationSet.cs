using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.State;

/// <summary>Contains the validated migration chain for one module-owned namespace.</summary>
/// <param name="ModuleId">The owning module identifier.</param>
/// <param name="Namespace">The namespace local identifier.</param>
/// <param name="ProviderCategory">The required storage provider category.</param>
/// <param name="Migrations">The ordered linear migration chain.</param>
public sealed record ModuleMigrationSet(
    ModuleId ModuleId,
    string Namespace,
    string ProviderCategory,
    IReadOnlyList<ModuleMigration> Migrations);
