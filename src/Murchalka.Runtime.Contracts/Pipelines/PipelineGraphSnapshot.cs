namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Contains one atomically published pipeline graph revision.</summary>
/// <param name="Revision">The monotonic in-process graph revision.</param>
/// <param name="Pipelines">The active pipeline graphs.</param>
public sealed record PipelineGraphSnapshot(long Revision, IReadOnlyList<PipelineSnapshot> Pipelines);
