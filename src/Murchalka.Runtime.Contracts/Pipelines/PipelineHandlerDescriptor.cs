using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Pipelines;

/// <summary>Identifies one active pipeline handler and its execution policy.</summary>
/// <param name="PipelineId">The pipeline identifier.</param>
/// <param name="StageId">The stage identifier.</param>
/// <param name="HandlerId">The handler identifier.</param>
/// <param name="ModuleId">The contributing module.</param>
/// <param name="ModuleVersion">The contributing module version.</param>
/// <param name="InstanceId">The authenticated runtime instance.</param>
/// <param name="LogicalInstance">The stable logical instance name.</param>
/// <param name="FailureMode">The handler failure policy.</param>
/// <param name="Timeout">The handler timeout.</param>
public sealed record PipelineHandlerDescriptor(
    string PipelineId,
    string StageId,
    string HandlerId,
    ModuleId ModuleId,
    SemanticVersion ModuleVersion,
    InstanceId InstanceId,
    string LogicalInstance,
    PipelineFailureMode FailureMode,
    TimeSpan Timeout);
