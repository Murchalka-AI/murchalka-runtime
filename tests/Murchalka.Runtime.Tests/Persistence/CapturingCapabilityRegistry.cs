using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Tests.Persistence;

internal sealed class CapturingCapabilityRegistry : ICapabilityRegistry
{
    public List<InvocationEnvelope> Invocations { get; } = [];
    public void Register(ModuleManifest manifest, InstanceId instanceId, string contentPath, string bundleDigest) => throw new NotSupportedException();
    public void Unregister(ModuleId moduleId, InstanceId instanceId) => throw new NotSupportedException();
    public IReadOnlyList<CapabilityProvider> Snapshot() => [];
    public Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken)
    {
        Invocations.Add(invocation);
        return Task.FromResult(new ResultEnvelope(invocation.InvocationId, InvocationStatus.Succeeded, JsonSerializer.SerializeToElement(new { version = "1" }), null, null, [], [], invocation.IdempotencyKey));
    }
}
