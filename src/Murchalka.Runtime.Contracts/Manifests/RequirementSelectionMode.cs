namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Defines how compatible dependency providers are selected.</summary>
public enum RequirementSelectionMode
{
    /// <summary>An administrator must resolve ambiguity.</summary>
    Admin,
    /// <summary>The Runtime applies its deterministic selection policy.</summary>
    Automatic,
    /// <summary>The Runtime applies deterministic preferences unless an administrator overrides them.</summary>
    Preferred,
    /// <summary>The consumer receives the authorized candidate set for per-invocation selection.</summary>
    ConsumerPolicy,
    /// <summary>An explicit binding is required for the applicable scope.</summary>
    Scoped
}
