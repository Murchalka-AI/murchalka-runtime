using System.Text.Json;

namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Contains the runtime-owned payload sent to a pipeline contribution.</summary>
/// <param name="PipelineId">The pipeline identifier.</param>
/// <param name="PipelineVersion">The pipeline definition version.</param>
/// <param name="StageId">The stage identifier.</param>
/// <param name="HandlerId">The handler identifier.</param>
/// <param name="Value">The current stage value.</param>
public sealed record PipelineHandlerInvocation(
    string PipelineId,
    int PipelineVersion,
    string StageId,
    string HandlerId,
    JsonElement Value);
