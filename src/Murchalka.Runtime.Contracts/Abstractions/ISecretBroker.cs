using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.Contracts.Secrets;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Issues audited secret leases constrained by a verified manifest and effective grant.</summary>
public interface ISecretBroker
{
    /// <summary>Issues a short-lived lease to one authenticated module instance.</summary>
    /// <param name="bundle">The verified installed bundle.</param>
    /// <param name="grant">The effective permission grant.</param>
    /// <param name="request">The untrusted module request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The bounded secret lease.</returns>
    Task<SecretLease> LeaseAsync(InstalledBundle bundle, PermissionDecision grant, SecretLeaseRequest request, CancellationToken cancellationToken);
}
