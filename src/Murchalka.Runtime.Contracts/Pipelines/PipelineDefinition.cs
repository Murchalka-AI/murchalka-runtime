using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Defines a versioned product-neutral pipeline and its schemas.</summary>
/// <param name="Id">The pipeline identifier.</param>
/// <param name="Version">The pipeline definition version.</param>
/// <param name="OwnerModule">The module that owns the definition.</param>
/// <param name="OwnerModuleVersion">The owning module version.</param>
/// <param name="InputSchemaPath">The resolved input schema path.</param>
/// <param name="OutputSchemaPath">The resolved output schema path.</param>
/// <param name="InputSchemaDigest">The input schema SHA-256 digest.</param>
/// <param name="OutputSchemaDigest">The output schema SHA-256 digest.</param>
/// <param name="Stages">The stages in execution order.</param>
/// <param name="Deadline">The maximum end-to-end execution duration.</param>
/// <param name="CancellationRequired">Whether handlers must honor cancellation.</param>
/// <param name="Checkpointing">The declared checkpointing behavior.</param>
public sealed record PipelineDefinition(
    string Id,
    int Version,
    ModuleId OwnerModule,
    SemanticVersion OwnerModuleVersion,
    string InputSchemaPath,
    string OutputSchemaPath,
    string InputSchemaDigest,
    string OutputSchemaDigest,
    IReadOnlyList<PipelineStageDefinition> Stages,
    TimeSpan Deadline,
    bool CancellationRequired,
    string Checkpointing);
