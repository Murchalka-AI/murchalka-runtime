using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Events;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Permissions;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Provides durable, schema-validated, at-least-once local module events.</summary>
public interface IEventFabric : IAsyncDisposable
{
    /// <summary>Starts durable outbox processing.</summary>
    /// <param name="cancellationToken">Cancels startup.</param>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Registers one healthy module's publications and subscriptions.</summary>
    /// <param name="manifest">The verified module manifest.</param>
    /// <param name="instanceId">The authenticated module instance.</param>
    /// <param name="contentPath">The immutable bundle content path.</param>
    /// <param name="permission">The effective permission decision.</param>
    void RegisterModule(ModuleManifest manifest, InstanceId instanceId, string contentPath, PermissionDecision permission);

    /// <summary>Removes one module instance from event routing.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="instanceId">The authenticated module instance.</param>
    void UnregisterModule(ModuleId moduleId, InstanceId instanceId);

    /// <summary>Validates and atomically appends one event to the durable outbox.</summary>
    /// <param name="request">The publication request.</param>
    /// <param name="cancellationToken">Cancels the append.</param>
    /// <returns>The canonical Runtime-stamped envelope.</returns>
    Task<EventEnvelope> PublishAsync(EventPublishRequest request, CancellationToken cancellationToken);

    /// <summary>Attempts every currently due outbox delivery once.</summary>
    /// <param name="cancellationToken">Cancels dispatch.</param>
    /// <returns>The number of acknowledged or quarantined deliveries.</returns>
    Task<int> DispatchPendingAsync(CancellationToken cancellationToken);

    /// <summary>Lists quarantined deliveries without payload content.</summary>
    /// <param name="cancellationToken">Cancels enumeration.</param>
    /// <returns>The quarantine metadata ordered by time.</returns>
    Task<IReadOnlyList<EventQuarantineItem>> GetQuarantineAsync(CancellationToken cancellationToken);

    /// <summary>Moves one quarantined delivery back to the durable outbox.</summary>
    /// <param name="quarantineId">The quarantine item identifier.</param>
    /// <param name="cancellationToken">Cancels replay.</param>
    /// <returns><see langword="true"/> when the item was queued for replay.</returns>
    Task<bool> ReplayAsync(string quarantineId, CancellationToken cancellationToken);
}
