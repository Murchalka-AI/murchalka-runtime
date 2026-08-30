using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Events.Internal;

internal sealed record OutboxTarget(
    ModuleId ConsumerModule,
    string HandlerId,
    int Attempts,
    DateTimeOffset? NextAttemptAt);
