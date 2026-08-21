namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Declares one manifest-authoritative handler contribution to a pipeline stage.</summary>
/// <param name="PipelineId">The target pipeline identifier.</param>
/// <param name="StageId">The target stage identifier.</param>
/// <param name="HandlerId">The module-local handler identifier.</param>
/// <param name="After">The handlers that must run before this handler.</param>
/// <param name="Before">The handlers that must run after this handler.</param>
/// <param name="FailureMode">The failure behavior.</param>
/// <param name="Timeout">The handler timeout.</param>
public sealed record PipelineContribution(
    string PipelineId,
    string StageId,
    string HandlerId,
    IReadOnlySet<string> After,
    IReadOnlySet<string> Before,
    PipelineFailureMode FailureMode,
    TimeSpan Timeout);
