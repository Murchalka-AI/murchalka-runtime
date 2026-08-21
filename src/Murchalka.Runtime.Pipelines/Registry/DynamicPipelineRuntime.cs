using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Pipelines;
using Murchalka.Runtime.Pipelines.Internal;

namespace Murchalka.Runtime.Pipelines.Registry;

/// <summary>Builds immutable dependency-ordered pipeline snapshots and executes them.</summary>
public sealed class DynamicPipelineRuntime : IPipelineRuntime
{
    private readonly object _gate = new();
    private readonly IPipelineHandlerInvoker _invoker;
    private readonly IRootAudit _audit;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<(ModuleId ModuleId, InstanceId InstanceId), ModulePipelineRegistration> _modules = [];
    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);
    private BindingDocument _bindings = BindingDocument.Empty("local");
    private PipelineGraphSnapshot _snapshot = new(0, []);

    /// <summary>Creates a dynamic pipeline runtime.</summary>
    /// <param name="invoker">The authenticated handler invoker.</param>
    /// <param name="audit">The non-disableable Root audit.</param>
    /// <param name="timeProvider">The optional trusted time provider.</param>
    public DynamicPipelineRuntime(IPipelineHandlerInvoker invoker, IRootAudit audit, TimeProvider? timeProvider = null)
    {
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public void RegisterModule(ModuleManifest manifest, InstanceId instanceId, string contentPath, BindingDocument bindings)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(bindings);
        var registration = new ModulePipelineRegistration(
            manifest.Id,
            manifest.Version,
            instanceId,
            PipelineDefinitionReader.ReadAll(manifest, contentPath),
            manifest.PipelineContributions);
        lock (_gate)
        {
            var key = (manifest.Id, instanceId);
            if (_modules.ContainsKey(key)) throw new InvalidOperationException($"Pipeline contributions for instance '{instanceId}' are already registered.");
            _modules.Add(key, registration);
            var priorBindings = _bindings;
            _bindings = bindings;
            try { PublishRebuild(); }
            catch
            {
                _modules.Remove(key);
                _bindings = priorBindings;
                throw;
            }
        }
    }

    /// <inheritdoc />
    public void UnregisterModule(ModuleId moduleId, InstanceId instanceId)
    {
        lock (_gate)
        {
            if (!_modules.Remove((moduleId, instanceId))) return;
            PublishRebuild();
        }
    }

    /// <inheritdoc />
    public void Rebuild(BindingDocument bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        lock (_gate)
        {
            _bindings = bindings;
            PublishRebuild();
        }
    }

    /// <inheritdoc />
    public PipelineGraphSnapshot Snapshot() => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public async Task<JsonElement> ExecuteAsync(PipelineExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pipeline = Snapshot().Pipelines.SingleOrDefault(value => string.Equals(value.Definition.Id, request.PipelineId, StringComparison.Ordinal))
            ?? throw new PipelineExecutionException("pipeline-not-found", $"Pipeline '{request.PipelineId}' is not active.");
        if (!pipeline.IsExecutable)
        {
            var issues = string.Join(",", pipeline.Stages.Where(value => value.Issue is not null).Select(value => value.Issue));
            throw new PipelineExecutionException("pipeline-unavailable", $"Pipeline '{request.PipelineId}' is unavailable: {issues}.");
        }
        Validate(GetSchema(pipeline.Definition.InputSchemaDigest, pipeline.Definition.InputSchemaPath), request.Input, "input");
        var now = _timeProvider.GetUtcNow();
        var deadline = request.Deadline < now.Add(pipeline.Definition.Deadline) ? request.Deadline : now.Add(pipeline.Definition.Deadline);
        if (deadline <= now) throw new TimeoutException("Pipeline deadline has elapsed.");
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        execution.CancelAfter(deadline - now);
        var value = request.Input.Clone();
        try
        {
            foreach (var stage in pipeline.Stages)
                value = await ExecuteStageAsync(pipeline.Definition, stage, value, request, deadline, execution.Token).ConfigureAwait(false);
            Validate(GetSchema(pipeline.Definition.OutputSchemaDigest, pipeline.Definition.OutputSchemaPath), value, "output");
            await _audit.AppendAsync("pipeline.executed", pipeline.Definition.OwnerModule.Value, "success", "pipeline-completed", new Dictionary<string, string?>
            {
                ["pipeline"] = pipeline.Definition.Id,
                ["version"] = pipeline.Definition.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["consumer"] = request.ConsumerModule.Value,
                ["graphRevision"] = Snapshot().Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);
            return value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && execution.IsCancellationRequested)
        {
            throw new TimeoutException($"Pipeline '{pipeline.Definition.Id}' exceeded its effective deadline.");
        }
    }

    private async Task<JsonElement> ExecuteStageAsync(
        PipelineDefinition definition,
        PipelineStageSnapshot stage,
        JsonElement value,
        PipelineExecutionRequest request,
        DateTimeOffset pipelineDeadline,
        CancellationToken cancellationToken) => stage.Definition.Mode switch
        {
            PipelineStageMode.Sequential or PipelineStageMode.Reduce => await ExecuteSequentialAsync(definition, stage, value, request, pipelineDeadline, cancellationToken).ConfigureAwait(false),
            PipelineStageMode.ParallelMerge => await ExecuteParallelMergeAsync(definition, stage, value, request, pipelineDeadline, cancellationToken).ConfigureAwait(false),
            PipelineStageMode.FirstSuccessful => await ExecuteFirstSuccessfulAsync(definition, stage, value, request, pipelineDeadline, cancellationToken).ConfigureAwait(false),
            PipelineStageMode.ExactlyOne => await InvokeAsync(definition, stage.Handlers.Single(), value, request, pipelineDeadline, cancellationToken).ConfigureAwait(false),
            PipelineStageMode.FanOut => await ExecuteFanOutAsync(definition, stage, value, request, pipelineDeadline, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage.Definition.Mode, "Unknown pipeline stage mode.")
        };

    private async Task<JsonElement> ExecuteSequentialAsync(PipelineDefinition definition, PipelineStageSnapshot stage, JsonElement value, PipelineExecutionRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var accumulator = value;
        foreach (var handler in stage.Handlers)
        {
            try { accumulator = await InvokeAsync(definition, handler, accumulator, request, deadline, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (CanContinue(handler, exception)) { }
        }
        return accumulator;
    }

    private async Task<JsonElement> ExecuteFirstSuccessfulAsync(PipelineDefinition definition, PipelineStageSnapshot stage, JsonElement value, PipelineExecutionRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var handler in stage.Handlers)
        {
            try { return await InvokeAsync(definition, handler, value, request, deadline, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException) { last = exception; }
        }
        throw new PipelineExecutionException("pipeline-no-successful-handler", $"No handler in stage '{stage.Definition.Id}' succeeded: {last?.Message}");
    }

    private async Task<JsonElement> ExecuteFanOutAsync(PipelineDefinition definition, PipelineStageSnapshot stage, JsonElement value, PipelineExecutionRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var tasks = stage.Handlers.Select(handler => InvokeWithPolicyAsync(definition, handler, value, request, deadline, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(results.Where(result => result.HasValue).Select(result => result!.Value).ToArray());
    }

    private async Task<JsonElement> ExecuteParallelMergeAsync(PipelineDefinition definition, PipelineStageSnapshot stage, JsonElement value, PipelineExecutionRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new PipelineExecutionException("pipeline-merge-input-invalid", $"Parallel merge stage '{stage.Definition.Id}' requires an object accumulator.");
        var tasks = stage.Handlers.Select(handler => InvokeWithPolicyAsync(definition, handler, value, request, deadline, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var merged = JsonNode.Parse(value.GetRawText())!.AsObject();
        foreach (var result in results.Where(result => result.HasValue).Select(result => result!.Value))
        {
            if (result.ValueKind != JsonValueKind.Object) throw new PipelineExecutionException("pipeline-merge-result-invalid", $"Parallel merge stage '{stage.Definition.Id}' received a non-object handler result.");
            foreach (var property in result.EnumerateObject())
            {
                var node = JsonNode.Parse(property.Value.GetRawText());
                if (merged.TryGetPropertyValue(property.Name, out var existing) && !JsonNode.DeepEquals(existing, node))
                    throw new PipelineExecutionException("pipeline-merge-conflict", $"Parallel merge stage '{stage.Definition.Id}' produced conflicting property '{property.Name}'.");
                merged[property.Name] = node;
            }
        }
        return JsonSerializer.SerializeToElement(merged);
    }

    private async Task<JsonElement?> InvokeWithPolicyAsync(PipelineDefinition definition, PipelineHandlerDescriptor handler, JsonElement value, PipelineExecutionRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        try { return await InvokeAsync(definition, handler, value, request, deadline, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (CanContinue(handler, exception)) { return null; }
    }

    private async Task<JsonElement> InvokeAsync(PipelineDefinition definition, PipelineHandlerDescriptor handler, JsonElement value, PipelineExecutionRequest request, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var effective = deadline < now.Add(handler.Timeout) ? deadline : now.Add(handler.Timeout);
        if (effective <= now) throw new TimeoutException($"Pipeline handler '{handler.HandlerId}' deadline has elapsed.");
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handlerCancellation.CancelAfter(effective - now);
        try { return await _invoker.InvokeAsync(handler, definition.Version, value, request, effective, handlerCancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && handlerCancellation.IsCancellationRequested) { throw new TimeoutException($"Pipeline handler '{handler.HandlerId}' timed out."); }
    }

    private static bool CanContinue(PipelineHandlerDescriptor handler, Exception exception) =>
        handler.FailureMode is (PipelineFailureMode.Continue or PipelineFailureMode.Fallback) && exception is not OperationCanceledException;

    private void PublishRebuild()
    {
        var definitions = _modules.Values.SelectMany(value => value.Definitions).GroupBy(value => value.Id, StringComparer.Ordinal).ToArray();
        var pipelines = new List<PipelineSnapshot>(definitions.Length);
        foreach (var group in definitions.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var distinct = group.Select(value => (
                    value.Version,
                    value.InputSchemaDigest,
                    value.OutputSchemaDigest,
                    Stages: string.Join('|', value.Stages.Select(stage => $"{stage.Id}:{stage.Mode}")),
                    value.Deadline,
                    value.CancellationRequired,
                    value.Checkpointing))
                .Distinct().ToArray();
            if (distinct.Length != 1)
                throw new PipelineExecutionException("pipeline-definition-conflict", $"Active modules define incompatible versions or schemas for pipeline '{group.Key}'.");
            var definition = group.OrderBy(value => value.OwnerModule.Value, StringComparer.Ordinal).First();
            pipelines.Add(BuildPipeline(definition));
        }
        var next = new PipelineGraphSnapshot(checked(_snapshot.Revision + 1), pipelines);
        Volatile.Write(ref _snapshot, next);
    }

    private PipelineSnapshot BuildPipeline(PipelineDefinition definition)
    {
        var contributions = _modules.Values.SelectMany(module => module.Contributions.Select(contribution => (module, contribution)))
            .Where(value => string.Equals(value.contribution.PipelineId, definition.Id, StringComparison.Ordinal)).ToArray();
        var unknownStage = contributions.FirstOrDefault(value => definition.Stages.All(stage => !string.Equals(stage.Id, value.contribution.StageId, StringComparison.Ordinal)));
        if (unknownStage != default)
            throw new PipelineExecutionException("pipeline-stage-not-found", $"Handler '{unknownStage.contribution.HandlerId}' targets unknown stage '{unknownStage.contribution.StageId}' in pipeline '{definition.Id}'.");
        var stages = definition.Stages.Select(stage => BuildStage(definition, stage, contributions.Where(value => value.contribution.StageId == stage.Id).ToArray())).ToArray();
        return new PipelineSnapshot(definition, stages, stages.All(value => value.Issue is null));
    }

    private PipelineStageSnapshot BuildStage(PipelineDefinition definition, PipelineStageDefinition stage, IReadOnlyList<(ModulePipelineRegistration module, PipelineContribution contribution)> values)
    {
        var duplicate = values.GroupBy(value => value.contribution.HandlerId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new PipelineExecutionException("pipeline-handler-conflict", $"Pipeline '{definition.Id}' stage '{stage.Id}' contains duplicate handler '{duplicate.Key}'.");
        var ordered = TopologicalOrder(definition, stage, values);
        string? issue = null;
        if (stage.Mode == PipelineStageMode.ExactlyOne)
        {
            if (ordered.Count == 0) issue = "exactly-one-handler-missing";
            else if (ordered.Count > 1)
            {
                var selected = SelectExactlyOne(definition, stage, ordered);
                if (selected is null) issue = "pending-binding";
                else ordered = [selected];
            }
        }
        else if (stage.Mode == PipelineStageMode.FirstSuccessful && ordered.Count == 0) issue = "first-successful-handler-missing";
        return new PipelineStageSnapshot(stage, ordered, issue);
    }

    private PipelineHandlerDescriptor? SelectExactlyOne(PipelineDefinition definition, PipelineStageDefinition stage, IReadOnlyList<PipelineHandlerDescriptor> handlers)
    {
        var binding = _bindings.Bindings.SingleOrDefault(value =>
            value.ConsumerModule == definition.OwnerModule &&
            string.Equals(value.RequirementId, stage.Id, StringComparison.Ordinal) &&
            value.Scope.Type == BindingScopeType.Global);
        if (binding is null) return null;
        return handlers.SingleOrDefault(handler =>
            handler.ModuleId == binding.Provider.Primary.ModuleId &&
            string.Equals(binding.Provider.Primary.CapabilityId.Value, definition.Id, StringComparison.Ordinal) &&
            string.Equals(binding.Provider.Primary.Instance, handler.LogicalInstance, StringComparison.Ordinal));
    }

    private static List<PipelineHandlerDescriptor> TopologicalOrder(PipelineDefinition definition, PipelineStageDefinition stage, IReadOnlyList<(ModulePipelineRegistration module, PipelineContribution contribution)> values)
    {
        var byId = values.ToDictionary(value => value.contribution.HandlerId, StringComparer.Ordinal);
        var edges = byId.Keys.ToDictionary(key => key, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        var incoming = byId.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        foreach (var value in values)
        {
            foreach (var after in value.contribution.After.Where(byId.ContainsKey)) AddEdge(after, value.contribution.HandlerId, edges, incoming);
            foreach (var before in value.contribution.Before.Where(byId.ContainsKey)) AddEdge(value.contribution.HandlerId, before, edges, incoming);
        }
        var available = new SortedSet<string>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        var result = new List<PipelineHandlerDescriptor>(values.Count);
        while (available.Count > 0)
        {
            var id = available.Min!;
            available.Remove(id);
            var value = byId[id];
            result.Add(new PipelineHandlerDescriptor(
                definition.Id,
                stage.Id,
                id,
                value.module.ModuleId,
                value.module.ModuleVersion,
                value.module.InstanceId,
                "default",
                value.contribution.FailureMode,
                value.contribution.Timeout));
            foreach (var target in edges[id])
                if (--incoming[target] == 0) available.Add(target);
        }
        if (result.Count != values.Count)
        {
            var cycle = string.Join(",", incoming.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal));
            throw new PipelineExecutionException("pipeline-order-cycle", $"Pipeline '{definition.Id}' stage '{stage.Id}' contains an ordering cycle: {cycle}.");
        }
        return result;
    }

    private static void AddEdge(string source, string target, Dictionary<string, HashSet<string>> edges, Dictionary<string, int> incoming)
    {
        if (edges[source].Add(target)) incoming[target]++;
    }

    private JsonSchema GetSchema(string digest, string path)
    {
        lock (_gate)
        {
            if (_schemas.TryGetValue(digest, out var schema)) return schema;
            schema = JsonSchema.FromText(File.ReadAllText(path));
            _schemas.Add(digest, schema);
            return schema;
        }
    }

    private static void Validate(JsonSchema schema, JsonElement value, string direction)
    {
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > 1024 * 1024)
            throw new PipelineExecutionException($"pipeline-{direction}-too-large", $"Pipeline {direction} exceeds the one MiB limit.");
        var result = schema.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.Flag, RequireFormatValidation = true });
        if (!result.IsValid) throw new PipelineExecutionException($"pipeline-{direction}-schema-invalid", $"Pipeline {direction} does not satisfy its declared schema.");
    }
}
