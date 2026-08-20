using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Lifecycle;

/// <summary>Provides data for an unexpected module process exit event.</summary>
public sealed class ModuleExitedEventArgs : EventArgs
{
    /// <summary>Creates module process exit event data.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="instanceId">The exited instance identifier.</param>
    /// <param name="exitCode">The operating-system process exit code.</param>
    /// <param name="reasonCode">The normalized exit reason.</param>
    public ModuleExitedEventArgs(ModuleId moduleId, InstanceId instanceId, int exitCode, string reasonCode)
    {
        ModuleId = moduleId;
        InstanceId = instanceId;
        ExitCode = exitCode;
        ReasonCode = reasonCode;
    }

    /// <summary>Gets the module identifier.</summary>
    public ModuleId ModuleId { get; }

    /// <summary>Gets the exited instance identifier.</summary>
    public InstanceId InstanceId { get; }

    /// <summary>Gets the operating-system process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Gets the normalized exit reason.</summary>
    public string ReasonCode { get; }
}
