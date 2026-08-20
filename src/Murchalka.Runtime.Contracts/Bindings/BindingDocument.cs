namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Contains one validated, revisioned installation binding document.</summary>
/// <param name="Installation">The installation identifier.</param>
/// <param name="Revision">The monotonic binding revision.</param>
/// <param name="Bindings">The explicit bindings.</param>
/// <param name="Policies">The fail-closed binding policies.</param>
public sealed record BindingDocument(string Installation, long Revision, IReadOnlyList<ModuleBinding> Bindings, BindingPolicies Policies)
{
    /// <summary>Creates the empty fail-closed binding document for an installation.</summary>
    /// <param name="installation">The installation identifier.</param>
    /// <returns>An empty revision-zero document.</returns>
    public static BindingDocument Empty(string installation) => new(installation, 0, [], new BindingPolicies(false, false));
}
