namespace Murchalka.Runtime.Contracts.Dependencies;

/// <summary>Classifies a complete fail-closed dependency resolution outcome.</summary>
public enum DependencyResolutionState
{
    /// <summary>Every active required dependency is resolved.</summary>
    Resolved,
    /// <summary>A required module or provider is unavailable.</summary>
    PendingDependencies,
    /// <summary>An explicit administrator binding is required.</summary>
    PendingBinding,
    /// <summary>The manifest does not request authority to invoke a required provider.</summary>
    PendingPermission,
    /// <summary>An installed dependency exists but no compatible version matches.</summary>
    Incompatible,
    /// <summary>A declared conflict or hard dependency cycle prevents activation.</summary>
    Conflict
}
