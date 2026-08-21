using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Events;

namespace Murchalka.Runtime.Events.Delivery;

/// <summary>Routes event deliveries to authenticated module sessions.</summary>
public sealed class ModuleEventDeliverySink : IEventDeliverySink
{
    private readonly IModuleSupervisor _supervisor;

    /// <summary>Creates a module event delivery sink.</summary>
    /// <param name="supervisor">The module supervisor that owns authenticated sessions.</param>
    public ModuleEventDeliverySink(IModuleSupervisor supervisor) =>
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    /// <inheritdoc />
    public async Task DeliverAsync(EventDelivery delivery, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var session = _supervisor.GetSession(delivery.ConsumerInstance) ?? throw new InvalidOperationException("Event subscriber session is unavailable.");
        var payload = JsonSerializer.SerializeToElement(delivery, ProtocolJson.Options);
        var invocation = new InvocationEnvelope(
            Guid.NewGuid(),
            new CapabilityId(delivery.Event.Topic),
            new SemanticVersion(delivery.Event.SchemaVersion, 0, 0),
            delivery.ConsumerInstance,
            delivery.Event.ProducerModule,
            delivery.Event.ActorReference,
            new InvocationScope(delivery.Event.TenantId, null, null, null, null, null),
            delivery.Event.Purpose,
            delivery.GrantReference,
            delivery.Event.CorrelationId,
            delivery.Event.CorrelationId,
            delivery.Event.CausationId,
            deadline,
            delivery.Event.EventId.ToString("D"),
            "runtime:event-delivery:v1",
            payload,
            null);
        var result = await session.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.Status != InvocationStatus.Succeeded)
            throw new InvalidOperationException($"Event handler '{delivery.HandlerId}' returned '{result.Status}'.");
    }
}
