using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Pipelines;

namespace Murchalka.Runtime.Pipelines.Internal;

internal sealed record ModulePipelineRegistration(
    ModuleId ModuleId,
    SemanticVersion ModuleVersion,
    InstanceId InstanceId,
    IReadOnlyList<PipelineDefinition> Definitions,
    IReadOnlyList<PipelineContribution> Contributions);
