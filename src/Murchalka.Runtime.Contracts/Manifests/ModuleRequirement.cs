using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes an exact-module dependency or conflict.</summary>
/// <param name="ModuleId">The required module identifier.</param>
/// <param name="VersionRange">The accepted module version range.</param>
/// <param name="Reason">The optional human-readable reason.</param>
public sealed record ModuleRequirement(ModuleId ModuleId, VersionRangeExpression VersionRange, string? Reason);
