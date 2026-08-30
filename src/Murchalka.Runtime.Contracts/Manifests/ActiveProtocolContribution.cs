using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Associates an active verified module with one external protocol contribution.</summary>
/// <param name="ModuleId">The provider module identifier.</param>
/// <param name="ModuleVersion">The provider module version.</param>
/// <param name="Contribution">The validated contribution.</param>
public sealed record ActiveProtocolContribution(
    ModuleId ModuleId,
    SemanticVersion ModuleVersion,
    ProtocolContribution Contribution);
