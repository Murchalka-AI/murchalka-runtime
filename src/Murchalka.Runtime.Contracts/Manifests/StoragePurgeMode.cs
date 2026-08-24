namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Specifies when module-owned storage may be purged.</summary>
public enum StoragePurgeMode
{
    /// <summary>Requires a separate explicit purge operation.</summary>
    Explicit,

    /// <summary>Allows purge during uninstall only with explicit approval.</summary>
    OnUninstallWithApproval,

    /// <summary>Retains the state when the bundle is removed.</summary>
    Retain
}
