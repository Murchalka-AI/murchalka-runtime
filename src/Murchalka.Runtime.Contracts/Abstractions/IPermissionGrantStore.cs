using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Permissions;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Evaluates separately administered permission grants for verified bundles.</summary>
public interface IPermissionGrantStore
{
    /// <summary>Evaluates whether a bundle has an effective permission grant.</summary>
    /// <param name="bundle">The verified bundle.</param>
    /// <param name="cancellationToken">Cancels evaluation.</param>
    /// <returns>The fail-closed permission decision.</returns>
    Task<PermissionDecision> EvaluateAsync(VerifiedBundle bundle, CancellationToken cancellationToken);
}
