using Murchalka.Runtime.Contracts.Secrets;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Provides encrypted-at-rest secret persistence for the Root secret broker.</summary>
public interface ISecretStore : IDisposable
{
    /// <summary>Gets and decrypts one secret revision.</summary>
    /// <param name="name">The stable secret name.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The secret material, or <see langword="null"/> when absent.</returns>
    Task<SecretMaterial?> GetAsync(string name, CancellationToken cancellationToken);

    /// <summary>Encrypts and atomically stores a secret revision.</summary>
    /// <param name="name">The stable secret name.</param>
    /// <param name="value">The secret bytes.</param>
    /// <param name="expectedRevision">The revision observed by the administrator.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Metadata for the committed revision.</returns>
    Task<SecretVersion> PutAsync(string name, ReadOnlyMemory<byte> value, long expectedRevision, CancellationToken cancellationToken);
}
