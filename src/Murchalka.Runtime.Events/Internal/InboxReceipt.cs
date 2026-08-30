using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Events.Internal;

internal sealed record InboxReceipt(
    Guid EventId,
    ModuleId ConsumerModule,
    string HandlerId,
    DateTimeOffset AcknowledgedAt);
