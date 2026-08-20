namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Writes non-disableable, redacted Root Trust audit events.</summary>
public interface IRootAudit
{
    /// <summary>Appends an event to the durable Root audit.</summary>
    /// <param name="eventType">The stable event type.</param>
    /// <param name="subject">The audited subject identifier.</param>
    /// <param name="outcome">The operation outcome.</param>
    /// <param name="reasonCode">The machine-readable reason code.</param>
    /// <param name="details">Optional redacted metadata.</param>
    /// <param name="cancellationToken">Cancels the append operation.</param>
    /// <returns>A task representing the durable append.</returns>
    ValueTask AppendAsync(string eventType, string subject, string outcome, string reasonCode, IReadOnlyDictionary<string, string?>? details = null, CancellationToken cancellationToken = default);
}
