using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Events;

/// <summary>Contains a request to append one event to the durable Runtime outbox.</summary>
/// <param name="EventId">The producer-generated idempotency identifier.</param>
/// <param name="Topic">The declared event topic.</param>
/// <param name="SchemaVersion">The payload schema version.</param>
/// <param name="ProducerModule">The authenticated producer module.</param>
/// <param name="ProducerInstance">The authenticated producer instance.</param>
/// <param name="OccurredAt">The time the fact occurred.</param>
/// <param name="TenantId">The optional tenant identifier.</param>
/// <param name="ActorReference">The optional actor reference.</param>
/// <param name="CorrelationId">The correlation identifier.</param>
/// <param name="CausationId">The optional causation identifier.</param>
/// <param name="PartitionKey">The declared ordering partition.</param>
/// <param name="DataClassification">The payload classification.</param>
/// <param name="Purpose">The declared processing purpose.</param>
/// <param name="Payload">The event payload.</param>
public sealed record EventPublishRequest(
    Guid EventId,
    string Topic,
    int SchemaVersion,
    ModuleId ProducerModule,
    InstanceId ProducerInstance,
    DateTimeOffset OccurredAt,
    string? TenantId,
    string? ActorReference,
    string CorrelationId,
    string? CausationId,
    string PartitionKey,
    DataClassification DataClassification,
    string Purpose,
    JsonElement Payload);
