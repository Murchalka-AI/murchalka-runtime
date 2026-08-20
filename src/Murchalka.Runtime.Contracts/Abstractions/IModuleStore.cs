using Murchalka.Runtime.Contracts.Bundles;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Stores verified bundles in a content-addressed immutable store.</summary>
public interface IModuleStore : IDisposable
{
    /// <summary>Installs a verified bundle atomically.</summary>
    /// <param name="bundle">The verified bundle.</param>
    /// <param name="cancellationToken">Cancels installation.</param>
    /// <returns>The installed bundle descriptor.</returns>
    Task<InstalledBundle> InstallAsync(VerifiedBundle bundle, CancellationToken cancellationToken);

    /// <summary>Opens an installed bundle by its authenticated digest.</summary>
    /// <param name="digest">The bundle SHA-256 identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The installed descriptor, or <see langword="null"/> when absent.</returns>
    Task<InstalledBundle?> OpenAsync(string digest, CancellationToken cancellationToken);
}
