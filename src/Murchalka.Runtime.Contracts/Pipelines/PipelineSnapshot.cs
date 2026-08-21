namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Contains an immutable executable or unavailable pipeline graph.</summary>
/// <param name="Definition">The active pipeline definition.</param>
/// <param name="Stages">The ordered stage snapshots.</param>
/// <param name="IsExecutable">Whether the graph can currently execute.</param>
public sealed record PipelineSnapshot(
    PipelineDefinition Definition,
    IReadOnlyList<PipelineStageSnapshot> Stages,
    bool IsExecutable);
