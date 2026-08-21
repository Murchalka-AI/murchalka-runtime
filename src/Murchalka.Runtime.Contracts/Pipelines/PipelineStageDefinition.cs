namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Defines one ordered stage in a dynamic pipeline.</summary>
/// <param name="Id">The stage identifier.</param>
/// <param name="Mode">The stage execution mode.</param>
public sealed record PipelineStageDefinition(string Id, PipelineStageMode Mode);
