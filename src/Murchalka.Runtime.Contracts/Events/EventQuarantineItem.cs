using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Events;

/// <summary>Describes a quarantined event delivery without exposing its payload.</summary>
/// <param name="Id">The replay identifier.</param>
/// <param name="EventId">The event identifier.</param>
/// <param name="Topic">The event topic.</param>
/// <param name="ConsumerModule">The target module.</param>
/// <param name="HandlerId">The target handler.</param>
/// <param name="ReasonCode">The normalized quarantine reason.</param>
/// <param name="QuarantinedAt">The quarantine time.</param>
public sealed record EventQuarantineItem(
    string Id,
    Guid EventId,
    string Topic,
    ModuleId ConsumerModule,
    string HandlerId,
    string ReasonCode,
    DateTimeOffset QuarantinedAt);
