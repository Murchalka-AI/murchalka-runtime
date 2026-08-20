using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Identifies one exact administrative binding scope.</summary>
/// <param name="Type">The scope type.</param>
/// <param name="Id">The scope identifier, or <see langword="null"/> for the global scope.</param>
public sealed record BindingScope(BindingScopeType Type, string? Id)
{
    /// <summary>Gets the installation-wide binding scope.</summary>
    public static BindingScope Global { get; } = new(BindingScopeType.Global, null);
}
