using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Lifecycle;
using Murchalka.Runtime.Contracts.Permissions;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Starts and stops isolated module instances.</summary>
public interface IModuleSupervisor
{
    /// <summary>Occurs when a module process exits unexpectedly.</summary>
    event EventHandler<ModuleExitedEventArgs>? ModuleExited;

    /// <summary>Starts an installed bundle and completes its authenticated handshake.</summary>
    /// <param name="bundle">The installed bundle.</param>
    /// <param name="grant">The effective permission decision.</param>
    /// <param name="configuration">The validated immutable configuration snapshot.</param>
    /// <param name="dependencies">The resolved dependency endpoints.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The authenticated gateway session.</returns>
    Task<IModuleGatewaySession> StartAsync(InstalledBundle bundle, PermissionDecision grant, ConfigurationSnapshot configuration, DependencyEndpointsSnapshot dependencies, CancellationToken cancellationToken);

    /// <summary>Drains and stops an instance.</summary>
    /// <param name="instanceId">The instance to stop.</param>
    /// <param name="drainTimeout">The maximum drain duration.</param>
    /// <param name="cancellationToken">Cancels the stop request.</param>
    /// <returns>A task representing the stop operation.</returns>
    Task StopAsync(InstanceId instanceId, TimeSpan drainTimeout, CancellationToken cancellationToken);

    /// <summary>Gets an authenticated session for a running instance.</summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <returns>The session, or <see langword="null"/> when unavailable.</returns>
    IModuleGatewaySession? GetSession(InstanceId instanceId);
}
