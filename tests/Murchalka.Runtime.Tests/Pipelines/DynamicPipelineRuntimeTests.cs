using System.Text.Json;
using System.Text.Json.Nodes;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Pipelines;
using Murchalka.Runtime.Pipelines.Registry;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Pipelines;

/// <summary>Verifies dynamic pipeline graph composition and live contribution changes.</summary>
public sealed class DynamicPipelineRuntimeTests
{
    /// <summary>Verifies that an optional relationship-style contributor attaches and detaches atomically.</summary>
    [Fact]
    public async Task OptionalContributorAttachesAndDetachesWithAtomicGraphRebuilds()
    {
        using var directory = new TestDirectory();
        Phase3TestModuleFactory.WritePipelineDefinition(directory.Path);
        var invoker = new RecordingPipelineInvoker();
        var runtime = new DynamicPipelineRuntime(invoker, new NoopRootAudit());
        var owner = Phase3TestModuleFactory.Create(
            "dev.murchalka.agent",
            ["agent.context.pipeline.json"],
            [Contribution("identity", after: new HashSet<string>(StringComparer.Ordinal))]);
        var relationship = Phase3TestModuleFactory.Create(
            "dev.murchalka.relationships",
            pipelineContributions: [Contribution("relationship-context", after: new HashSet<string>(["identity"], StringComparer.Ordinal))]);
        var ownerInstance = new InstanceId("agent-1");
        var relationshipInstance = new InstanceId("relationships-1");
        runtime.RegisterModule(owner, ownerInstance, directory.Path, BindingDocument.Empty("local"));

        var firstRevision = runtime.Snapshot().Revision;
        var before = await runtime.ExecuteAsync(Request(), CancellationToken.None);
        Assert.Equal(["identity"], invoker.Invocations);
        Assert.True(before.GetProperty("identity").GetBoolean());

        invoker.Invocations.Clear();
        runtime.RegisterModule(relationship, relationshipInstance, directory.Path, BindingDocument.Empty("local"));
        Assert.True(runtime.Snapshot().Revision > firstRevision);
        var attached = await runtime.ExecuteAsync(Request(), CancellationToken.None);
        Assert.Equal(["identity", "relationship-context"], invoker.Invocations);
        Assert.True(attached.GetProperty("relationship-context").GetBoolean());

        invoker.Invocations.Clear();
        runtime.UnregisterModule(relationship.Id, relationshipInstance);
        var detached = await runtime.ExecuteAsync(Request(), CancellationToken.None);
        Assert.Equal(["identity"], invoker.Invocations);
        Assert.False(detached.TryGetProperty("relationship-context", out _));
    }

    /// <summary>Verifies that a DAG cycle rejects the candidate without replacing the active graph.</summary>
    [Fact]
    public void OrderingCycleRejectsRegistrationWithoutPublishingPartialGraph()
    {
        using var directory = new TestDirectory();
        Phase3TestModuleFactory.WritePipelineDefinition(directory.Path);
        var runtime = new DynamicPipelineRuntime(new RecordingPipelineInvoker(), new NoopRootAudit());
        var owner = Phase3TestModuleFactory.Create(
            "dev.murchalka.agent",
            ["agent.context.pipeline.json"],
            [Contribution("identity", after: new HashSet<string>(["relationship-context"], StringComparer.Ordinal))]);
        runtime.RegisterModule(owner, new InstanceId("agent-1"), directory.Path, BindingDocument.Empty("local"));
        var prior = runtime.Snapshot();
        var relationship = Phase3TestModuleFactory.Create(
            "dev.murchalka.relationships",
            pipelineContributions: [Contribution("relationship-context", after: new HashSet<string>(["identity"], StringComparer.Ordinal))]);

        var exception = Assert.Throws<PipelineExecutionException>(() =>
            runtime.RegisterModule(relationship, new InstanceId("relationships-1"), directory.Path, BindingDocument.Empty("local")));

        Assert.Equal("pipeline-order-cycle", exception.ReasonCode);
        Assert.Same(prior, runtime.Snapshot());
    }

    /// <summary>Verifies fail-closed exactly-one ambiguity and explicit binding selection.</summary>
    [Fact]
    public void ExactlyOneStageRequiresAndAppliesExplicitBinding()
    {
        using var directory = new TestDirectory();
        Phase3TestModuleFactory.WritePipelineDefinition(directory.Path, "exactlyOne");
        var runtime = new DynamicPipelineRuntime(new RecordingPipelineInvoker(), new NoopRootAudit());
        var owner = Phase3TestModuleFactory.Create("dev.murchalka.agent", ["agent.context.pipeline.json"]);
        var identity = Phase3TestModuleFactory.Create("dev.murchalka.identity", pipelineContributions: [Contribution("identity", after: new HashSet<string>(StringComparer.Ordinal))]);
        var relationship = Phase3TestModuleFactory.Create("dev.murchalka.relationships", pipelineContributions: [Contribution("relationship-context", after: new HashSet<string>(StringComparer.Ordinal))]);
        runtime.RegisterModule(owner, new InstanceId("agent-1"), directory.Path, BindingDocument.Empty("local"));
        runtime.RegisterModule(identity, new InstanceId("identity-1"), directory.Path, BindingDocument.Empty("local"));
        runtime.RegisterModule(relationship, new InstanceId("relationships-1"), directory.Path, BindingDocument.Empty("local"));
        Assert.False(runtime.Snapshot().Pipelines.Single().IsExecutable);
        Assert.Equal("pending-binding", runtime.Snapshot().Pipelines.Single().Stages.Single().Issue);

        var binding = new BindingDocument("local", 1,
        [
            new ModuleBinding(
                "agent-enrich",
                owner.Id,
                "enrich",
                BindingScope.Global,
                new ProviderSelection(new ProviderReference(relationship.Id, new CapabilityId("agent.context"), "default"), [], new HashSet<string>(), 0))
        ], new BindingPolicies(false, false));
        runtime.Rebuild(binding);

        var handler = runtime.Snapshot().Pipelines.Single().Stages.Single().Handlers.Single();
        Assert.Equal(relationship.Id, handler.ModuleId);
        Assert.True(runtime.Snapshot().Pipelines.Single().IsExecutable);
    }

    private static PipelineContribution Contribution(string handler, IReadOnlySet<string> after) => new(
        "agent.context",
        "enrich",
        handler,
        after,
        new HashSet<string>(StringComparer.Ordinal),
        PipelineFailureMode.Fail,
        TimeSpan.FromSeconds(1));

    private static PipelineExecutionRequest Request() => new(
        "agent.context",
        JsonSerializer.SerializeToElement(new { }),
        new ModuleId("dev.murchalka.consumer"),
        null,
        new InvocationScope("home", null, null, null, null, null),
        "test",
        "grant:test",
        "trace",
        "correlation",
        null,
        DateTimeOffset.UtcNow.AddSeconds(10));

    private sealed class RecordingPipelineInvoker : IPipelineHandlerInvoker
    {
        public List<string> Invocations { get; } = [];

        public Task<JsonElement> InvokeAsync(PipelineHandlerDescriptor handler, int pipelineVersion, JsonElement value, PipelineExecutionRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
        {
            Invocations.Add(handler.HandlerId);
            var result = JsonNode.Parse(value.GetRawText())!.AsObject();
            result[handler.HandlerId] = true;
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
    }
}
