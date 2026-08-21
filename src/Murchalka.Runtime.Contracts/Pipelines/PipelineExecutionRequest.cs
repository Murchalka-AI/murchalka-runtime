using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Contains one authorized pipeline execution request.</summary>
/// <param name="PipelineId">The pipeline identifier.</param>
/// <param name="Input">The schema-validated pipeline input.</param>
/// <param name="ConsumerModule">The invoking module.</param>
/// <param name="ActorReference">The optional actor reference.</param>
/// <param name="Scope">The invocation scope.</param>
/// <param name="Purpose">The declared purpose.</param>
/// <param name="AuthorizationGrantReference">The authorization grant reference.</param>
/// <param name="TraceId">The trace identifier.</param>
/// <param name="CorrelationId">The correlation identifier.</param>
/// <param name="CausationId">The optional causation identifier.</param>
/// <param name="Deadline">The caller deadline.</param>
public sealed record PipelineExecutionRequest(
    string PipelineId,
    JsonElement Input,
    ModuleId ConsumerModule,
    string? ActorReference,
    InvocationScope Scope,
    string Purpose,
    string AuthorizationGrantReference,
    string TraceId,
    string CorrelationId,
    string? CausationId,
    DateTimeOffset Deadline);
