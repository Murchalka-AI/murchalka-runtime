namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Specifies how the Runtime applies a validated module configuration change.</summary>
public enum ConfigurationRestartPolicy
{
    /// <summary>Applies the new snapshot to the active module without restarting it.</summary>
    Reload,

    /// <summary>Restarts only the affected module.</summary>
    RestartModule,

    /// <summary>Requires an explicit target restart and is not applied live.</summary>
    RestartTarget,

    /// <summary>Rejects changes after the first stored revision.</summary>
    Immutable
}
