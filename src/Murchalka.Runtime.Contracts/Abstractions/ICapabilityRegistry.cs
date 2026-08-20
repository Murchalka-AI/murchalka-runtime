using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Registers active providers and routes validated capability invocations.</summary>
public interface ICapabilityRegistry
{
    /// <summary>Registers all manifest-declared capabilities for an active instance.</summary>
    /// <param name="manifest">The verified module manifest.</param>
    /// <param name="instanceId">The authenticated provider instance.</param>
    /// <param name="contentPath">The immutable bundle content path.</param>
    /// <param name="bundleDigest">The authenticated provider bundle digest.</param>
    void Register(ModuleManifest manifest, InstanceId instanceId, string contentPath, string bundleDigest);

    /// <summary>Removes every capability owned by the specified instance.</summary>
    /// <param name="moduleId">The owning module.</param>
    /// <param name="instanceId">The provider instance.</param>
    void Unregister(ModuleId moduleId, InstanceId instanceId);

    /// <summary>Returns a stable snapshot of active capability providers.</summary>
    /// <returns>The active providers.</returns>
    IReadOnlyList<CapabilityProvider> Snapshot();

    /// <summary>Validates and routes a capability invocation.</summary>
    /// <param name="invocation">The invocation envelope.</param>
    /// <param name="cancellationToken">Cancels the invocation.</param>
    /// <returns>The provider result.</returns>
    Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken);
}
