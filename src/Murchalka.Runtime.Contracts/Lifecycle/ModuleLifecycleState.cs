namespace Murchalka.Runtime.Contracts.Lifecycle;

/// <summary>Defines the durable lifecycle states of a module bundle or instance.</summary>
public enum ModuleLifecycleState
{
    /// <summary>The bundle was discovered in the inbox.</summary>
    Discovered,
    /// <summary>The bundle is being moved into staging.</summary>
    Staging,
    /// <summary>The bundle is undergoing Root Trust verification.</summary>
    Verifying,
    /// <summary>The bundle requires an explicit publisher trust decision.</summary>
    AwaitingTrust,
    /// <summary>The module graph is being resolved.</summary>
    Resolving,
    /// <summary>Required dependencies are unavailable.</summary>
    PendingDependencies,
    /// <summary>An ambiguous requirement needs an administrator binding.</summary>
    PendingBinding,
    /// <summary>The module requires a valid permission grant.</summary>
    PendingPermission,
    /// <summary>An installed dependency exists but no compatible version can satisfy the module.</summary>
    Incompatible,
    /// <summary>A dependency cycle or declared module conflict prevents activation.</summary>
    Conflict,
    /// <summary>The verified bundle is being installed.</summary>
    Installing,
    /// <summary>Module-owned state migrations are running.</summary>
    Migrating,
    /// <summary>The selected artifact is starting.</summary>
    Starting,
    /// <summary>The authenticated instance is passing its health gate.</summary>
    HealthChecking,
    /// <summary>The module is healthy and routable.</summary>
    Active,
    /// <summary>The module no longer accepts new routing and is draining work.</summary>
    Draining,
    /// <summary>The module is installed but disabled.</summary>
    Disabled,
    /// <summary>A side-by-side update is in progress.</summary>
    Updating,
    /// <summary>The module failed activation or execution.</summary>
    Failed,
    /// <summary>The bundle is isolated because it failed a trust or safety check.</summary>
    Quarantined,
    /// <summary>The bundle is no longer installed while data is retained by default.</summary>
    Uninstalled
}
