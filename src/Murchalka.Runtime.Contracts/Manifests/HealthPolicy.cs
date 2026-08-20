namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Defines startup and readiness health limits for a module.</summary>
/// <param name="StartupTimeout">The complete startup timeout.</param>
/// <param name="ReadinessTimeout">The timeout for one readiness probe.</param>
/// <param name="ReadinessFailureThreshold">The maximum failed readiness probes.</param>
public sealed record HealthPolicy(TimeSpan StartupTimeout, TimeSpan ReadinessTimeout, int ReadinessFailureThreshold);
