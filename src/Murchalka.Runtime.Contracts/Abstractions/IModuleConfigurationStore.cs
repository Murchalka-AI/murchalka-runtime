using System.Text.Json;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Configuration;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Validates and persists revisioned module configuration snapshots.</summary>
public interface IModuleConfigurationStore : IDisposable
{
    /// <summary>Loads the effective configuration for an installed bundle.</summary>
    /// <param name="bundle">The installed bundle containing the signed schema and defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The validated effective snapshot.</returns>
    Task<ModuleConfigurationSnapshot> GetAsync(InstalledBundle bundle, CancellationToken cancellationToken);

    /// <summary>Validates and atomically replaces administrator-provided configuration values.</summary>
    /// <param name="bundle">The installed bundle containing the signed schema and defaults.</param>
    /// <param name="values">The untrusted configuration values.</param>
    /// <param name="expectedRevision">The revision observed by the caller.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The committed effective snapshot.</returns>
    Task<ModuleConfigurationSnapshot> ReplaceAsync(
        InstalledBundle bundle,
        JsonElement values,
        long expectedRevision,
        CancellationToken cancellationToken);
}
