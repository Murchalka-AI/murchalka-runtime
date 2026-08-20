namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Contains an explicit primary provider and any declared failover providers.</summary>
/// <param name="Primary">The primary provider.</param>
/// <param name="Failover">The ordered explicit failover providers.</param>
/// <param name="AllowedDataClasses">The data classes permitted during failover.</param>
/// <param name="MaximumAttempts">The maximum failover attempts.</param>
public sealed record ProviderSelection(
    ProviderReference Primary,
    IReadOnlyList<ProviderReference> Failover,
    IReadOnlySet<string> AllowedDataClasses,
    int MaximumAttempts);
