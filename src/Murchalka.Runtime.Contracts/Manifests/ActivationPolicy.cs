namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Defines module activation and failure behavior.</summary>
/// <param name="Mode">The automatic or manual activation mode.</param>
/// <param name="FailurePolicy">The activation failure policy.</param>
/// <param name="HotReload">Whether the module declares hot reload support.</param>
/// <param name="DrainTimeout">The maximum graceful drain duration.</param>
public sealed record ActivationPolicy(string Mode, string FailurePolicy, bool HotReload, TimeSpan DrainTimeout);
