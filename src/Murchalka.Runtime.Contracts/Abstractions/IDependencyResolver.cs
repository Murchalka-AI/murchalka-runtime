using Murchalka.Runtime.Contracts.Dependencies;

namespace Murchalka.Runtime.Contracts.Abstractions;

/// <summary>Resolves module, capability, category, cardinality, and binding requirements.</summary>
public interface IDependencyResolver
{
    /// <summary>Resolves all active requirements without using installation order.</summary>
    /// <param name="request">The immutable resolution inputs.</param>
    /// <returns>The complete deterministic resolution outcome.</returns>
    DependencyResolutionResult Resolve(DependencyResolutionRequest request);
}
