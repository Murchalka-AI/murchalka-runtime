using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Events.Internal;

internal sealed record QuarantineRecord(
    string Id,
    EventEnvelope Event,
    OutboxTarget Target,
    string ReasonCode,
    DateTimeOffset QuarantinedAt);
