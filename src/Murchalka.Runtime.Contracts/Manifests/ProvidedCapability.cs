using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes a capability declared by a verified module manifest.</summary>
/// <param name="Id">The capability identifier.</param>
/// <param name="Category">The capability category.</param>
/// <param name="Version">The capability version.</param>
/// <param name="ContractPath">The bundle-relative contract path.</param>
/// <param name="Timeout">The declared invocation timeout.</param>
public sealed record ProvidedCapability(CapabilityId Id, string Category, SemanticVersion Version, string ContractPath, TimeSpan Timeout);
