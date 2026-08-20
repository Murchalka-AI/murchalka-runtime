using System.Diagnostics;
using System.Text;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.ModuleGateway.Listeners;
using Murchalka.Runtime.ModuleGateway.Sessions;

namespace Murchalka.Runtime.ModuleSupervisor.Internal;

internal sealed class ManagedModule
{
    /// <summary>Creates process state tracked by the module supervisor.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="instanceId">The module instance identifier.</param>
    /// <param name="process">The operating-system process.</param>
    /// <param name="listener">The process gateway listener.</param>
    public ManagedModule(ModuleId moduleId, InstanceId instanceId, Process process, ModuleGatewayListener listener)
    {
        ModuleId = moduleId;
        InstanceId = instanceId;
        Process = process;
        Listener = listener;
    }

    /// <summary>Gets the module identifier.</summary>
    public ModuleId ModuleId { get; }
    /// <summary>Gets the module instance identifier.</summary>
    public InstanceId InstanceId { get; }
    /// <summary>Gets the operating-system process.</summary>
    public Process Process { get; }
    /// <summary>Gets the process gateway listener.</summary>
    public ModuleGatewayListener Listener { get; }
    /// <summary>Gets or sets the authenticated gateway session.</summary>
    public ModuleGatewaySession? Session { get; set; }
    /// <summary>Gets or sets whether an intentional stop is in progress.</summary>
    public bool Stopping { get; set; }
    /// <summary>Gets or sets the standard-output drain task.</summary>
    public Task? OutputDrain { get; set; }
    /// <summary>Gets or sets the standard-error drain task.</summary>
    public Task? ErrorDrain { get; set; }
    /// <summary>Gets the retained tail of standard-error output.</summary>
    public StringBuilder ErrorTail { get; } = new();
}
