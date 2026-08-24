namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Specifies whether an upgrade requires state migration.</summary>
public enum StateMigrationRequirement
{
    /// <summary>The module has no state migration step.</summary>
    None,

    /// <summary>The Runtime applies migrations when a storage namespace is declared.</summary>
    Optional,

    /// <summary>The Runtime must complete all declared migrations before activation.</summary>
    Required
}
