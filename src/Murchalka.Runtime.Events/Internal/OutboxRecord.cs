using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Events.Internal;

internal sealed record OutboxRecord(EventEnvelope Event, IReadOnlyList<OutboxTarget> Targets);

internal sealed record OutboxTarget(
    ModuleId ConsumerModule,
    string HandlerId,
    int Attempts,
    DateTimeOffset? NextAttemptAt);

internal sealed record QuarantineRecord(
    string Id,
    EventEnvelope Event,
    OutboxTarget Target,
    string ReasonCode,
    DateTimeOffset QuarantinedAt);

internal sealed record InboxReceipt(
    Guid EventId,
    ModuleId ConsumerModule,
    string HandlerId,
    DateTimeOffset AcknowledgedAt);
