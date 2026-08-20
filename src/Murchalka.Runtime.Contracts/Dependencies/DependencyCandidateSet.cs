using Murchalka.Runtime.Contracts.Capabilities;

namespace Murchalka.Runtime.Contracts.Dependencies;

/// <summary>Contains the deterministic compatible candidates for one requirement.</summary>
/// <param name="RequirementId">The requirement identifier.</param>
/// <param name="Candidates">The compatible candidates in deterministic policy order.</param>
public sealed record DependencyCandidateSet(string RequirementId, IReadOnlyList<CapabilityProvider> Candidates);
