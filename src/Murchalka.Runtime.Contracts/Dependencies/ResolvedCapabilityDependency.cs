using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Dependencies;

/// <summary>Describes one selected capability provider.</summary>
/// <param name="RequirementId">The consumer-local requirement identifier.</param>
/// <param name="ProviderModule">The provider module identifier.</param>
/// <param name="ProviderModuleVersion">The provider module version.</param>
/// <param name="ProviderBundleDigest">The provider bundle digest.</param>
/// <param name="CapabilityId">The selected capability identifier.</param>
/// <param name="CapabilityVersion">The selected capability version.</param>
/// <param name="LogicalInstance">The stable logical provider instance name.</param>
/// <param name="RuntimeInstance">The active process instance.</param>
/// <param name="BindingRevision">The binding revision that selected the provider.</param>
public sealed record ResolvedCapabilityDependency(
    string RequirementId,
    ModuleId ProviderModule,
    SemanticVersion ProviderModuleVersion,
    string ProviderBundleDigest,
    CapabilityId CapabilityId,
    SemanticVersion CapabilityVersion,
    string LogicalInstance,
    InstanceId RuntimeInstance,
    long BindingRevision);
