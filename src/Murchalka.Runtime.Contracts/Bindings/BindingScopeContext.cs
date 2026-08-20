using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Contains binding scopes ordered from most specific to least specific.</summary>
public sealed class BindingScopeContext
{
    /// <summary>Initializes a validated binding scope context.</summary>
    /// <param name="scopes">The scopes in most-specific-first order.</param>
    public BindingScopeContext(IEnumerable<BindingScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        Scopes = scopes.ToArray();
        if (Scopes.Count == 0 || Scopes[^1] != BindingScope.Global)
            throw new ArgumentException("A binding scope context must end with the global scope.", nameof(scopes));
        if (Scopes.Distinct().Count() != Scopes.Count)
            throw new ArgumentException("A binding scope context cannot contain duplicate scopes.", nameof(scopes));
    }

    /// <summary>Gets the scopes in most-specific-first order.</summary>
    public IReadOnlyList<BindingScope> Scopes { get; }

    /// <summary>Creates the activation context for a consuming module.</summary>
    /// <param name="moduleId">The consuming module identifier.</param>
    /// <returns>A module scope followed by the global scope.</returns>
    public static BindingScopeContext ForModule(ModuleId moduleId) => new([
        new BindingScope(Murchalka.Runtime.Contracts.Manifests.BindingScopeType.Module, moduleId.Value),
        BindingScope.Global
    ]);
}
