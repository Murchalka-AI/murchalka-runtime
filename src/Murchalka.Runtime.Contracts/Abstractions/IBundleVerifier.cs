using Murchalka.Runtime.Contracts.Bundles;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Verifies an untrusted module bundle before installation or execution.</summary>
public interface IBundleVerifier
{
    /// <summary>Verifies the staged bundle and returns its authenticated identity and manifest.</summary>
    /// <param name="stagedPath">The staged archive path.</param>
    /// <param name="cancellationToken">Cancels verification.</param>
    /// <returns>The verified bundle metadata.</returns>
    Task<VerifiedBundle> VerifyAsync(string stagedPath, CancellationToken cancellationToken);
}
