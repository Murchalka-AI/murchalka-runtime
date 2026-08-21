namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Contains one immutable, dependency-ordered pipeline stage.</summary>
/// <param name="Definition">The stage definition.</param>
/// <param name="Handlers">The ordered active handlers.</param>
/// <param name="Issue">The optional normalized composition issue.</param>
public sealed record PipelineStageSnapshot(
    PipelineStageDefinition Definition,
    IReadOnlyList<PipelineHandlerDescriptor> Handlers,
    string? Issue);
