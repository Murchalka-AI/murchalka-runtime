using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Tests.Security;

internal sealed class CapturingSecretCapabilityRegistry : ICapabilityRegistry
{
    private readonly IReadOnlyList<CapabilityProvider> _providers;
    private byte[]? _value;
    private long _revision;

    public CapturingSecretCapabilityRegistry(int providerCount = 1)
    {
        _providers = Enumerable.Range(0, providerCount).Select(index => new CapabilityProvider(
            new CapabilityId("secrets.local"),
            new SemanticVersion(1, 0, index),
            new ModuleId($"dev.murchalka.secrets-local-{index}"),
            new InstanceId($"secrets-local-{index}"),
            "secrets.provider",
            "contract.json",
            TimeSpan.FromSeconds(30),
            "default",
            new SemanticVersion(0, 1, 0),
            "sha256:" + new string('a', 64),
            new Dictionary<string, JsonElement>(),
            new HashSet<BindingScopeType> { BindingScopeType.Global })).ToArray();
    }

    public InvocationEnvelope? LastInvocation { get; private set; }

    public void Register(ModuleManifest manifest, InstanceId instanceId, string contentPath, string bundleDigest) =>
        throw new NotSupportedException();

    public void Unregister(ModuleId moduleId, InstanceId instanceId) =>
        throw new NotSupportedException();

    public IReadOnlyList<CapabilityProvider> Snapshot() => _providers;

    public Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastInvocation = invocation;
        var request = invocation.Payload!.Value;
        var operation = request.GetProperty("operation").GetString();
        JsonElement response;
        if (operation == "put")
        {
            _value = Convert.FromBase64String(request.GetProperty("value").GetString()!);
            _revision++;
            response = JsonSerializer.SerializeToElement(new
            {
                operation = "put",
                name = request.GetProperty("name").GetString(),
                revision = _revision,
                updatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            response = _value is null
                ? JsonSerializer.SerializeToElement(new
                {
                    operation = "get",
                    name = request.GetProperty("name").GetString(),
                    found = false
                })
                : JsonSerializer.SerializeToElement(new
                {
                    operation = "get",
                    name = request.GetProperty("name").GetString(),
                    found = true,
                    revision = _revision,
                    value = Convert.ToBase64String(_value),
                    updatedAt = DateTimeOffset.UtcNow
                });
        }

        return Task.FromResult(new ResultEnvelope(invocation.InvocationId, InvocationStatus.Succeeded, response, null, null, [], [], invocation.IdempotencyKey));
    }
}
