using System.Text.Json;
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

    /// <summary>Validates an untrusted signed permission grant without storing it.</summary>
    /// <param name="bundle">The verified bundle the grant must cover.</param>
    /// <param name="document">The complete signed grant document.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>The fail-closed permission decision.</returns>
    Task<PermissionDecision> ValidateAsync(VerifiedBundle bundle, JsonElement document, CancellationToken cancellationToken);
}
