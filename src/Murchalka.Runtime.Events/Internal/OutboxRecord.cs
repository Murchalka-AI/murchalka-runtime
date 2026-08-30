using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Events.Internal;

internal sealed record OutboxRecord(EventEnvelope Event, IReadOnlyList<OutboxTarget> Targets);
