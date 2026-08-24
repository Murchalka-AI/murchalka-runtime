using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Dependencies;
using Murchalka.Runtime.Contracts.State;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Coordinates provider-backed module state migrations and portable exports.</summary>
public interface IStateMigrationCoordinator : IDisposable
{
    /// <summary>Validates and applies every pending signed migration before module activation.</summary>
    /// <param name="bundle">The installed consumer bundle.</param>
    /// <param name="resolution">The resolved storage provider bindings.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing migration completion.</returns>
    Task ApplyPendingAsync(InstalledBundle bundle, DependencyResolutionResult resolution, CancellationToken cancellationToken);

    /// <summary>Exports one declared module-owned namespace through its bound provider.</summary>
    /// <param name="bundle">The installed consumer bundle.</param>
    /// <param name="resolution">The resolved storage provider bindings.</param>
    /// <param name="namespaceName">The declared namespace name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The authenticated export artifact.</returns>
    Task<StateExport> ExportAsync(InstalledBundle bundle, DependencyResolutionResult resolution, string namespaceName, CancellationToken cancellationToken);

    /// <summary>Imports a previously authenticated export through the bound provider.</summary>
    /// <param name="bundle">The installed consumer bundle.</param>
    /// <param name="resolution">The resolved storage provider bindings.</param>
    /// <param name="stateExport">The authenticated export artifact.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing validated import completion.</returns>
    Task ImportAsync(InstalledBundle bundle, DependencyResolutionResult resolution, StateExport stateExport, CancellationToken cancellationToken);

    /// <summary>Rolls a failed upgrade back to the prior bundle's schema version using signed down artifacts.</summary>
    /// <param name="candidate">The failed upgrade candidate bundle.</param>
    /// <param name="prior">The prior bundle being restored.</param>
    /// <param name="resolution">The candidate's resolved storage provider bindings.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task representing rollback completion.</returns>
    Task RollbackUpgradeAsync(InstalledBundle candidate, InstalledBundle prior, DependencyResolutionResult resolution, CancellationToken cancellationToken);
}
