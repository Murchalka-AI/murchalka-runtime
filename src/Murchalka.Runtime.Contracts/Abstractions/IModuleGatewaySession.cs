using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Secrets;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Represents an authenticated Module Protocol session.</summary>
public interface IModuleGatewaySession : IAsyncDisposable
{
    /// <summary>Gets the authenticated module hello message.</summary>
    ModuleHello Hello { get; }

    /// <summary>Gets the validated readiness declaration.</summary>
    ModuleReady Ready { get; }

    /// <summary>Gets the authenticated instance identifier.</summary>
    InstanceId InstanceId { get; }

    /// <summary>Requests the module health state.</summary>
    /// <param name="timeout">The health operation timeout.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The observed module health.</returns>
    Task<ModuleHealth> ProbeHealthAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Sends a lifecycle control operation.</summary>
    /// <param name="kind">The control operation kind.</param>
    /// <param name="timeout">The operation timeout.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The module control result.</returns>
    Task<ControlResult> SendControlAsync(ControlMessageKind kind, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Atomically applies a new resolved dependency snapshot.</summary>
    /// <param name="snapshot">The new immutable dependency snapshot.</param>
    /// <param name="timeout">The update operation timeout.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The module control result.</returns>
    Task<ControlResult> UpdateDependenciesAsync(DependencyEndpointsSnapshot snapshot, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Atomically applies a validated configuration snapshot.</summary>
    /// <param name="snapshot">The new immutable configuration snapshot.</param>
    /// <param name="timeout">The update operation timeout.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The module control result.</returns>
    Task<ControlResult> UpdateConfigurationAsync(ConfigurationSnapshot snapshot, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Registers the Runtime-owned handler for authenticated event publication frames.</summary>
    /// <param name="publisher">The durable event publisher.</param>
    void SetEventPublisher(Func<EventEnvelope, CancellationToken, Task<EventEnvelope>> publisher);

    /// <summary>Registers the Runtime-owned handler for bounded secret lease requests.</summary>
    /// <param name="broker">The Root secret lease broker.</param>
    void SetSecretBroker(Func<SecretLeaseRequest, CancellationToken, Task<SecretLease>> broker);

    /// <summary>Registers the Runtime-owned router for granted dependency capability calls.</summary>
    /// <param name="invoker">The validated Runtime capability invoker.</param>
    void SetDependencyInvoker(Func<InvocationEnvelope, CancellationToken, Task<ResultEnvelope>> invoker);

    /// <summary>Sends a capability invocation to the module.</summary>
    /// <param name="invocation">The invocation envelope.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The module result.</returns>
    Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken);
}
