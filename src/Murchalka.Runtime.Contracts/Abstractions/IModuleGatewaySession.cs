using Murchalka.ModuleProtocol.Contracts;

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

    /// <summary>Sends a capability invocation to the module.</summary>
    /// <param name="invocation">The invocation envelope.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The module result.</returns>
    Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken);
}
