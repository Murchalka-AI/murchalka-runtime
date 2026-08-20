using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Identifies one administratively selected capability provider.</summary>
/// <param name="ModuleId">The provider module identifier.</param>
/// <param name="CapabilityId">The provider capability identifier.</param>
/// <param name="Instance">The stable logical provider instance name.</param>
public sealed record ProviderReference(ModuleId ModuleId, CapabilityId CapabilityId, string Instance);
