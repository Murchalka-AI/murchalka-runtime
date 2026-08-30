using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes a bounded external protocol route contributed by a verified module.</summary>
/// <param name="Id">The globally unique contribution identifier.</param>
/// <param name="Version">The contribution contract major version.</param>
/// <param name="RouteNamespace">The gateway-owned route namespace.</param>
/// <param name="HandlerCapability">The bounded handler capability.</param>
/// <param name="DescriptorPath">The bundle-relative discovery descriptor.</param>
/// <param name="Transports">The declared external transports.</param>
/// <param name="AuthenticationSchemes">The declared authentication schemes.</param>
/// <param name="StreamingMode">The declared streaming shape.</param>
/// <param name="MaximumPayloadBytes">The maximum accepted payload size.</param>
/// <param name="MaximumConcurrency">The maximum concurrent requests.</param>
/// <param name="Timeout">The maximum request duration.</param>
public sealed record ProtocolContribution(
    string Id,
    int Version,
    string RouteNamespace,
    CapabilityId HandlerCapability,
    string DescriptorPath,
    IReadOnlySet<string> Transports,
    IReadOnlySet<string> AuthenticationSchemes,
    string StreamingMode,
    int MaximumPayloadBytes,
    int MaximumConcurrency,
    TimeSpan Timeout);
