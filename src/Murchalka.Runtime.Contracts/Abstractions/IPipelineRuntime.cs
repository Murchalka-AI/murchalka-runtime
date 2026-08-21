using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Pipelines;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Builds and executes immutable dynamic pipeline graphs.</summary>
public interface IPipelineRuntime
{
    /// <summary>Registers a healthy module's definitions and contributions and atomically rebuilds the graph.</summary>
    /// <param name="manifest">The verified module manifest.</param>
    /// <param name="instanceId">The authenticated module instance.</param>
    /// <param name="contentPath">The immutable bundle content path.</param>
    /// <param name="bindings">The current administrative bindings.</param>
    void RegisterModule(ModuleManifest manifest, InstanceId instanceId, string contentPath, BindingDocument bindings);

    /// <summary>Removes a module instance and atomically rebuilds every affected graph.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="instanceId">The authenticated module instance.</param>
    void UnregisterModule(ModuleId moduleId, InstanceId instanceId);

    /// <summary>Rebuilds exactly-one stage selections from a new binding revision.</summary>
    /// <param name="bindings">The current administrative bindings.</param>
    void Rebuild(BindingDocument bindings);

    /// <summary>Gets the current immutable graph snapshot.</summary>
    /// <returns>The current graph revision.</returns>
    PipelineGraphSnapshot Snapshot();

    /// <summary>Executes a schema-validated pipeline graph.</summary>
    /// <param name="request">The pipeline request.</param>
    /// <param name="cancellationToken">Cancels execution.</param>
    /// <returns>The schema-validated pipeline output.</returns>
    Task<JsonElement> ExecuteAsync(PipelineExecutionRequest request, CancellationToken cancellationToken);
}
