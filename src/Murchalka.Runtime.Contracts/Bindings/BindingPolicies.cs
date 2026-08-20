namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Contains installation-wide fail-closed binding policies.</summary>
/// <param name="InheritParentScopes">Whether a missing specific scope may inherit its parent scope.</param>
/// <param name="AllowDeclaredFailover">Whether only explicitly declared failover may be attempted.</param>
public sealed record BindingPolicies(bool InheritParentScopes, bool AllowDeclaredFailover);
