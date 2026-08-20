using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes a capability or category dependency declared by a module.</summary>
/// <param name="RequirementId">The consumer-local requirement identifier.</param>
/// <param name="CapabilityId">The exact capability identifier, when requested.</param>
/// <param name="Category">The capability category, when requested.</param>
/// <param name="VersionRange">The accepted capability version range.</param>
/// <param name="Qualifiers">The required provider qualifiers.</param>
/// <param name="Cardinality">The provider cardinality.</param>
/// <param name="Selection">The provider selection mode.</param>
/// <param name="Scope">The provider scope requirement.</param>
/// <param name="Fallback">The named optional fallback.</param>
/// <param name="Condition">The declarative activation condition.</param>
/// <param name="IsOptional">Whether absence is handled by a fallback.</param>
public sealed record CapabilityRequirement(
    string RequirementId,
    CapabilityId? CapabilityId,
    string? Category,
    VersionRangeExpression VersionRange,
    IReadOnlyDictionary<string, JsonElement> Qualifiers,
    RequirementCardinality Cardinality,
    RequirementSelectionMode Selection,
    BindingScopeType? Scope,
    string? Fallback,
    RequirementCondition? Condition,
    bool IsOptional);
