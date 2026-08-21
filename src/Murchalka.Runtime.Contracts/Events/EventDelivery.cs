using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Events;

/// <summary>Contains one durable event delivery addressed to a manifest handler.</summary>
/// <param name="Event">The event envelope.</param>
/// <param name="ConsumerModule">The subscribing module.</param>
/// <param name="ConsumerInstance">The active subscriber instance.</param>
/// <param name="HandlerId">The stable handler identifier.</param>
/// <param name="GrantReference">The subscriber permission grant reference.</param>
public sealed record EventDelivery(
    EventEnvelope Event,
    ModuleId ConsumerModule,
    InstanceId ConsumerInstance,
    string HandlerId,
    string GrantReference);
