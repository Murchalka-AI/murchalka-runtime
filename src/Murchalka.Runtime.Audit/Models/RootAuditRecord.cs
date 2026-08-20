namespace Murchalka.Runtime.Audit.Models;

/// <summary>Represents one immutable record in the Root audit hash chain.</summary>
/// <param name="Sequence">The monotonic record sequence.</param>
/// <param name="Timestamp">The trusted UTC timestamp.</param>
/// <param name="EventType">The stable event type.</param>
/// <param name="Subject">The audited subject.</param>
/// <param name="Outcome">The operation outcome.</param>
/// <param name="ReasonCode">The machine-readable reason.</param>
/// <param name="Details">The redacted event metadata.</param>
/// <param name="PreviousHash">The preceding record hash.</param>
/// <param name="RecordHash">The current record hash.</param>
public sealed record RootAuditRecord(long Sequence, DateTimeOffset Timestamp, string EventType, string Subject, string Outcome, string ReasonCode, IReadOnlyDictionary<string, string?> Details, string PreviousHash, string RecordHash);
