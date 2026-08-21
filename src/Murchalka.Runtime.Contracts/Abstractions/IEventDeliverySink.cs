using Murchalka.Runtime.Contracts.Events;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Delivers an event to one authenticated module handler.</summary>
public interface IEventDeliverySink
{
    /// <summary>Delivers an event and completes only after the handler acknowledges it.</summary>
    /// <param name="delivery">The addressed event delivery.</param>
    /// <param name="deadline">The effective delivery deadline.</param>
    /// <param name="cancellationToken">Cancels delivery.</param>
    Task DeliverAsync(EventDelivery delivery, DateTimeOffset deadline, CancellationToken cancellationToken);
}
