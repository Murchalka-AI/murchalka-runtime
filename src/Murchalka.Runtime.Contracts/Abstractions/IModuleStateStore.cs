using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Lifecycle;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Persists durable module lifecycle records.</summary>
public interface IModuleStateStore : IDisposable
{
    /// <summary>Saves a complete lifecycle record atomically.</summary>
    /// <param name="record">The lifecycle record.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    /// <returns>The persisted record.</returns>
    Task<InstalledModuleRecord> SaveAsync(InstalledModuleRecord record, CancellationToken cancellationToken);

    /// <summary>Gets the lifecycle record for a module.</summary>
    /// <param name="id">The module identifier.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The record, or <see langword="null"/> when absent.</returns>
    Task<InstalledModuleRecord?> GetAsync(ModuleId id, CancellationToken cancellationToken);

    /// <summary>Gets all durable lifecycle records.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The lifecycle records.</returns>
    Task<IReadOnlyList<InstalledModuleRecord>> GetAllAsync(CancellationToken cancellationToken);
}
