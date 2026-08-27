using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Capabilities.Internal;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Capabilities.Registry;

/// <summary>Provides manifest-authoritative capability registration and schema-validated routing.</summary>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly IModuleSupervisor _supervisor;
    private readonly IRootAudit _audit;
    private readonly ConcurrentDictionary<ProviderKey, RegisteredProvider> _providers = new();

    /// <summary>Initializes a capability registry.</summary>
    /// <param name="supervisor">The module process supervisor.</param>
    /// <param name="audit">The non-disableable Root audit.</param>
    public CapabilityRegistry(IModuleSupervisor supervisor, IRootAudit audit)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc/>
    public void Register(ModuleManifest manifest, InstanceId instanceId, string contentPath, string bundleDigest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (var capability in manifest.Capabilities)
        {
            if (capability.Targets is { } targets && !targets.Contains(ModuleTarget.Runtime)) continue;
            var contract = ResolveInside(contentPath, capability.ContractPath);
            if (!File.Exists(contract)) throw new InvalidDataException($"Declared capability contract '{capability.ContractPath}' is missing.");
            var provider = new CapabilityProvider(
                capability.Id,
                capability.Version,
                manifest.Id,
                instanceId,
                capability.Category,
                contract,
                capability.Timeout,
                "default",
                manifest.Version,
                bundleDigest,
                capability.Qualifiers,
                capability.Scopes);
            var policy = LoadPolicy(contract);
            if (!_providers.TryAdd(new ProviderKey(instanceId, capability.Id, capability.Version), new RegisteredProvider(provider, policy)))
            {
                Unregister(manifest.Id, instanceId);
                throw new InvalidOperationException($"Capability '{capability.Id}@{capability.Version}' is already registered for instance '{instanceId}'.");
            }
        }
    }

    /// <inheritdoc/>
    public void Unregister(ModuleId moduleId, InstanceId instanceId)
    {
        foreach (var pair in _providers.Where(pair => pair.Value.Provider.ModuleId == moduleId && pair.Value.Provider.InstanceId == instanceId).ToArray()) _providers.TryRemove(pair.Key, out _);
    }

    /// <inheritdoc/>
    public IReadOnlyList<CapabilityProvider> Snapshot() => _providers.Values.Select(value => value.Provider)
        .OrderBy(value => value.CapabilityId.Value, StringComparer.Ordinal)
        .ThenBy(value => value.Version)
        .ThenBy(value => value.ModuleId.Value, StringComparer.Ordinal)
        .ToArray();

    /// <inheritdoc/>
    public async Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var now = DateTimeOffset.UtcNow;
        if (invocation.Deadline <= now) throw new TimeoutException("Invocation deadline has elapsed.");
        var key = new ProviderKey(invocation.ProviderInstance, invocation.CapabilityId, invocation.CapabilityVersion);
        if (!_providers.TryGetValue(key, out var registered)) throw new KeyNotFoundException("Requested provider instance/capability is not active.");
        var provider = registered.Provider;
        ValidatePayload(registered.Policy.Request, invocation.Payload, registered.Policy.MaximumPayloadBytes, "request");
        var session = _supervisor.GetSession(provider.InstanceId) ?? throw new InvalidOperationException("Capability provider session is unavailable.");
        var effectiveDeadline = invocation.Deadline < now.Add(provider.Timeout) ? invocation.Deadline : now.Add(provider.Timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveDeadline - now);
        try
        {
            var result = await session.InvokeAsync(invocation with { Deadline = effectiveDeadline }, deadline.Token).ConfigureAwait(false);
            if (result.Status == InvocationStatus.Succeeded) ValidatePayload(registered.Policy.Response, result.Payload, registered.Policy.MaximumPayloadBytes, "response");
            await _audit.AppendAsync("capability.invoked", provider.ModuleId.Value, result.Status.ToString(), "invocation-completed", new Dictionary<string, string?>
            {
                ["capability"] = provider.CapabilityId.Value,
                ["version"] = provider.Version.ToString(),
                ["instance"] = provider.InstanceId.Value,
                ["invocationId"] = invocation.InvocationId.ToString("D"),
                ["consumer"] = invocation.ConsumerModuleId.Value
            }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await _audit.AppendAsync("capability.invoked", provider.ModuleId.Value, "cancelled", "invocation-deadline", new Dictionary<string, string?>
            {
                ["capability"] = provider.CapabilityId.Value,
                ["instance"] = provider.InstanceId.Value,
                ["invocationId"] = invocation.InvocationId.ToString("D")
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string ResolveInside(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Contract path escapes bundle content.");
        return path;
    }

    private static ContractPolicy LoadPolicy(string contractPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(contractPath));
        var root = document.RootElement;
        var directory = Path.GetDirectoryName(contractPath)!;
        var requestPath = ResolveInside(directory, root.GetProperty("request").GetProperty("schema").GetString()!);
        var responsePath = ResolveInside(directory, root.GetProperty("response").GetProperty("schema").GetString()!);
        var maximum = root.GetProperty("semantics").GetProperty("maxPayloadBytes").GetInt32();
        return new ContractPolicy(JsonSchema.FromText(File.ReadAllText(requestPath)), JsonSchema.FromText(File.ReadAllText(responsePath)), maximum);
    }

    private static void ValidatePayload(JsonSchema schema, JsonElement? payload, int maximumBytes, string direction)
    {
        var value = payload ?? JsonSerializer.SerializeToElement<object?>(null);
        if (Encoding.UTF8.GetByteCount(value.GetRawText()) > maximumBytes) throw new InvalidDataException($"Capability {direction} payload exceeds the declared limit.");
        var result = schema.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.Flag, RequireFormatValidation = true });
        if (!result.IsValid) throw new InvalidDataException($"Capability {direction} payload does not satisfy its declared schema.");
    }
}
