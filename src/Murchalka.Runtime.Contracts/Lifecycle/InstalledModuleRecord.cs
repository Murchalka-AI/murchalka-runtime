using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Bundles;

namespace Murchalka.Runtime.Contracts.Lifecycle;

/// <summary>Contains the durable lifecycle state for an installed module.</summary>
/// <param name="ModuleId">The module identifier.</param>
/// <param name="Version">The module version.</param>
/// <param name="BundleDigest">The authenticated bundle digest.</param>
/// <param name="Publisher">The verified publisher identifier.</param>
/// <param name="State">The current lifecycle state.</param>
/// <param name="Revision">The monotonic state revision.</param>
/// <param name="UpdatedAt">The last transition time.</param>
/// <param name="ReasonCode">The last transition reason.</param>
/// <param name="InstanceId">The active instance identifier, when any.</param>
/// <param name="DesiredEnabled">Whether reconciliation should keep the module enabled.</param>
public sealed record InstalledModuleRecord(ModuleId ModuleId, SemanticVersion Version, string BundleDigest, string Publisher, ModuleLifecycleState State, long Revision, DateTimeOffset UpdatedAt, string? ReasonCode, string? InstanceId, bool DesiredEnabled)
{
    /// <summary>Creates the first durable record for a verified bundle.</summary>
    /// <param name="bundle">The verified bundle.</param>
    /// <param name="now">The current trusted time.</param>
    /// <returns>The initial verification record.</returns>
    public static InstalledModuleRecord Create(VerifiedBundle bundle, DateTimeOffset now) => new(
        bundle.Manifest.Id, bundle.Manifest.Version, bundle.Identity.Digest, bundle.Identity.Publisher,
        ModuleLifecycleState.Verifying, 1, now, null, null, true);
}
