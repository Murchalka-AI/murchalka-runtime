using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Capabilities;

/// <summary>Describes an active capability provider.</summary>
/// <param name="CapabilityId">The capability identifier.</param>
/// <param name="Version">The capability version.</param>
/// <param name="ModuleId">The provider module identifier.</param>
/// <param name="InstanceId">The authenticated provider instance.</param>
/// <param name="Category">The capability category.</param>
/// <param name="ContractPath">The validated contract path.</param>
/// <param name="Timeout">The declared invocation timeout.</param>
public sealed record CapabilityProvider(CapabilityId CapabilityId, SemanticVersion Version, ModuleId ModuleId, InstanceId InstanceId, string Category, string ContractPath, TimeSpan Timeout);
