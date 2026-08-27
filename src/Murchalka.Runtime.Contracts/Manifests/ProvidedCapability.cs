using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes a capability declared by a verified module manifest.</summary>
/// <param name="Id">The capability identifier.</param>
/// <param name="Category">The capability category.</param>
/// <param name="Version">The capability version.</param>
/// <param name="ContractPath">The bundle-relative contract path.</param>
/// <param name="Timeout">The declared invocation timeout.</param>
/// <param name="Qualifiers">The provider qualifiers used during dependency resolution.</param>
/// <param name="Scopes">The scopes supported by the provider.</param>
/// <param name="Targets">The host tiers on which the capability is provided. A missing value preserves the v1 Runtime-only default.</param>
public sealed record ProvidedCapability(
    CapabilityId Id,
    string Category,
    SemanticVersion Version,
    string ContractPath,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, JsonElement> Qualifiers,
    IReadOnlySet<BindingScopeType> Scopes,
    IReadOnlySet<ModuleTarget>? Targets = null);
