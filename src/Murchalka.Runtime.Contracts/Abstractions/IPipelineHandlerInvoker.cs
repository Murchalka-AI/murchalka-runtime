using System.Text.Json;
using Murchalka.Runtime.Contracts.Pipelines;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Invokes one authenticated out-of-process pipeline contribution.</summary>
public interface IPipelineHandlerInvoker
{
    /// <summary>Invokes a pipeline handler within its effective deadline.</summary>
    /// <param name="handler">The active handler descriptor.</param>
    /// <param name="pipelineVersion">The pipeline definition version.</param>
    /// <param name="value">The current stage value.</param>
    /// <param name="request">The parent execution request.</param>
    /// <param name="deadline">The effective handler deadline.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The handler result value.</returns>
    Task<JsonElement> InvokeAsync(
        PipelineHandlerDescriptor handler,
        int pipelineVersion,
        JsonElement value,
        PipelineExecutionRequest request,
        DateTimeOffset deadline,
        CancellationToken cancellationToken);
}
