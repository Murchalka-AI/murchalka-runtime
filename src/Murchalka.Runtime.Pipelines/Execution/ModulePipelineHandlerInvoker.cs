using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Pipelines;

namespace Murchalka.Runtime.Pipelines.Execution;

/// <summary>Routes pipeline handler invocations to authenticated module sessions.</summary>
public sealed class ModulePipelineHandlerInvoker : IPipelineHandlerInvoker
{
    private readonly IModuleSupervisor _supervisor;

    /// <summary>Creates a module pipeline handler invoker.</summary>
    /// <param name="supervisor">The module supervisor that owns authenticated sessions.</param>
    public ModulePipelineHandlerInvoker(IModuleSupervisor supervisor) =>
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    /// <inheritdoc />
    public async Task<JsonElement> InvokeAsync(
        PipelineHandlerDescriptor handler,
        int pipelineVersion,
        JsonElement value,
        PipelineExecutionRequest request,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var session = _supervisor.GetSession(handler.InstanceId) ?? throw new InvalidOperationException("Pipeline handler session is unavailable.");
        var payload = JsonSerializer.SerializeToElement(new PipelineHandlerInvocation(
            handler.PipelineId,
            pipelineVersion,
            handler.StageId,
            handler.HandlerId,
            value));
        var invocation = new InvocationEnvelope(
            Guid.NewGuid(),
            new CapabilityId(handler.PipelineId),
            new SemanticVersion(pipelineVersion, 0, 0),
            handler.InstanceId,
            request.ConsumerModule,
            request.ActorReference,
            request.Scope,
            request.Purpose,
            request.AuthorizationGrantReference,
            request.TraceId,
            request.CorrelationId,
            request.CausationId,
            deadline,
            null,
            "runtime:pipeline-handler:v1",
            payload,
            null);
        var result = await session.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.Status != InvocationStatus.Succeeded)
            throw new PipelineExecutionException("pipeline-handler-rejected", $"Pipeline handler '{handler.HandlerId}' returned '{result.Status}'.");
        return result.Payload ?? JsonSerializer.SerializeToElement<object?>(null);
    }
}
