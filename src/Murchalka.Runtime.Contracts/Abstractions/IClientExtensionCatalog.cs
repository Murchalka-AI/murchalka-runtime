using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.ClientExtensions;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Publishes verified client artifacts for active modules.</summary>
public interface IClientExtensionCatalog
{
    /// <summary>Gets the current atomic catalog revision.</summary>
    ClientExtensionCatalogSnapshot Snapshot();

    /// <summary>Validates and atomically registers all client artifacts from an active module.</summary>
    /// <param name="bundle">The verified installed bundle.</param>
    void RegisterModule(InstalledBundle bundle);

    /// <summary>Removes every artifact owned by a disabled module.</summary>
    /// <param name="moduleId">The disabled module identifier.</param>
    void UnregisterModule(ModuleId moduleId);

    /// <summary>Gets immutable artifact bytes by content digest.</summary>
    /// <param name="digest">The canonical SHA-256 digest.</param>
    /// <returns>The artifact content, or <see langword="null"/> when inactive.</returns>
    ClientArtifactContent? OpenArtifact(string digest);

    /// <summary>Waits until a catalog revision newer than the observed revision is available.</summary>
    /// <param name="observedRevision">The last observed revision.</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>The newer catalog revision.</returns>
    ValueTask<long> WaitForRevisionAsync(long observedRevision, CancellationToken cancellationToken);
}
