namespace Murchalka.Runtime.Contracts.Dependencies;

/// <summary>Contains a complete dependency resolution outcome and diagnostics.</summary>
/// <param name="State">The resolution state.</param>
/// <param name="ReasonCode">The stable machine-readable reason.</param>
/// <param name="ModuleDependencies">The resolved exact-module dependencies.</param>
/// <param name="CapabilityDependencies">The selected capability providers.</param>
/// <param name="Fallbacks">The named optional fallbacks that are active.</param>
/// <param name="CandidateSets">The compatible candidates considered for each requirement.</param>
/// <param name="CyclePath">The complete cycle path when a hard dependency cycle is found.</param>
public sealed record DependencyResolutionResult(
    DependencyResolutionState State,
    string ReasonCode,
    IReadOnlyList<ResolvedModuleDependency> ModuleDependencies,
    IReadOnlyList<ResolvedCapabilityDependency> CapabilityDependencies,
    IReadOnlyDictionary<string, string> Fallbacks,
    IReadOnlyList<DependencyCandidateSet> CandidateSets,
    IReadOnlyList<string> CyclePath)
{
    /// <summary>Gets whether the result permits module activation.</summary>
    public bool Succeeded => State == DependencyResolutionState.Resolved;
}
