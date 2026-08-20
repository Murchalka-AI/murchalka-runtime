namespace Murchalka.Runtime.Contracts.Lifecycle;

/// <summary>Provides the public diagnostic view of a module lifecycle record.</summary>
/// <param name="ModuleId">The module identifier.</param>
/// <param name="Version">The module version.</param>
/// <param name="BundleDigest">The authenticated bundle digest.</param>
/// <param name="State">The lifecycle state.</param>
/// <param name="Revision">The monotonic state revision.</param>
/// <param name="UpdatedAt">The last transition time.</param>
/// <param name="ReasonCode">The last transition reason.</param>
/// <param name="InstanceId">The active instance identifier, when any.</param>
/// <param name="DesiredEnabled">Whether reconciliation should enable the module.</param>
public sealed record ModuleStatus(string ModuleId, string Version, string BundleDigest, ModuleLifecycleState State, long Revision, DateTimeOffset UpdatedAt, string? ReasonCode, string? InstanceId, bool DesiredEnabled);
