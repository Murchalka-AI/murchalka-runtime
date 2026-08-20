using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Dependencies;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Persists generated, reproducible Runtime composition locks.</summary>
public interface ICompositionLockStore
{
    /// <summary>Writes the resolved composition lock atomically.</summary>
    /// <param name="bundle">The installed consumer bundle.</param>
    /// <param name="resolution">The successful dependency resolution.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    /// <returns>The generated lock path.</returns>
    Task<string> WriteAsync(InstalledBundle bundle, DependencyResolutionResult resolution, CancellationToken cancellationToken);
}
