using System.Text.Json;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Dependencies;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Dependencies.Resolution;

/// <summary>Resolves dependency graphs deterministically and fails closed on ambiguity.</summary>
public sealed class DependencyResolver : IDependencyResolver
{
    /// <inheritdoc/>
    public DependencyResolutionResult Resolve(DependencyResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var modules = request.Modules
            .Where(value => value.Manifest.Id != request.Consumer.Id || value.BundleDigest != request.ConsumerBundleDigest)
            .ToArray();
        if (FindConflict(request.Consumer, modules) is { } conflict)
            return Failure(DependencyResolutionState.Conflict, $"module-conflict:{conflict}");
        if (FindCycle(request.Consumer, modules) is { Count: > 0 } cycle)
            return Failure(DependencyResolutionState.Conflict, "dependency-cycle", cycle);

        var resolvedModules = new List<ResolvedModuleDependency>();
        foreach (var requirement in request.Consumer.ModuleRequirements.OrderBy(value => value.ModuleId.Value, StringComparer.Ordinal))
        {
            var byId = modules.Where(value => value.Manifest.Id == requirement.ModuleId).ToArray();
            var compatible = byId.Where(value => requirement.VersionRange.Satisfies(value.Manifest.Version)).ToArray();
            if (compatible.Length == 0)
                return Failure(byId.Length == 0 ? DependencyResolutionState.PendingDependencies : DependencyResolutionState.Incompatible,
                    byId.Length == 0 ? $"module-missing:{requirement.ModuleId.Value}" : $"module-version-incompatible:{requirement.ModuleId.Value}");
            var active = compatible.Where(value => value.IsActive)
                .OrderByDescending(value => value.Manifest.Version)
                .ThenBy(value => value.BundleDigest, StringComparer.Ordinal)
                .ToArray();
            if (active.Length == 0)
                return Failure(DependencyResolutionState.PendingDependencies, $"module-unavailable:{requirement.ModuleId.Value}");
            var selected = active[0];
            resolvedModules.Add(new ResolvedModuleDependency(selected.Manifest.Id, selected.Manifest.Version, selected.BundleDigest));
        }

        var resolvedCapabilities = new List<ResolvedCapabilityDependency>();
        var fallbacks = new Dictionary<string, string>(StringComparer.Ordinal);
        var candidateSets = new List<DependencyCandidateSet>();
        foreach (var requirement in request.Consumer.CapabilityRequirements.Concat(request.Consumer.OptionalCapabilityRequirements)
                     .OrderBy(value => value.RequirementId, StringComparer.Ordinal))
        {
            if (!ConditionMatches(requirement, request.Configuration)) continue;
            var candidates = CompatibleProviders(requirement, request.Providers);
            candidateSets.Add(new DependencyCandidateSet(requirement.RequirementId, candidates));
            if (!HasInvocationPermission(request.Consumer, requirement))
            {
                if (UseFallback(requirement, fallbacks)) continue;
                return Failure(DependencyResolutionState.PendingPermission, $"capability-permission-missing:{requirement.RequirementId}", candidates: candidateSets);
            }

            var binding = FindBinding(request, requirement);
            if (binding is not null)
            {
                candidates = candidates.Where(value => Matches(value, binding.Provider.Primary)).ToArray();
                if (candidates.Length == 0)
                {
                    if (UseFallback(requirement, fallbacks)) continue;
                    return Failure(DependencyResolutionState.PendingDependencies, $"bound-provider-unavailable:{requirement.RequirementId}", candidates: candidateSets);
                }
            }

            if (candidates.Length == 0)
            {
                if (UseFallback(requirement, fallbacks) || AllowsZero(requirement.Cardinality)) continue;
                var declarations = DeclaredCandidates(requirement, modules);
                var hasCompatibleDeclaration = declarations.Any(value => requirement.VersionRange.Satisfies(value.Version) &&
                    (requirement.Scope is null || value.Scopes.Contains(requirement.Scope.Value)));
                var state = declarations.Length > 0 && !hasCompatibleDeclaration
                    ? DependencyResolutionState.Incompatible
                    : DependencyResolutionState.PendingDependencies;
                var reason = declarations.Length == 0
                    ? $"capability-missing:{requirement.RequirementId}"
                    : hasCompatibleDeclaration
                        ? $"capability-unavailable:{requirement.RequirementId}"
                        : $"capability-version-incompatible:{requirement.RequirementId}";
                return Failure(state, reason,
                    candidates: candidateSets);
            }

            if (binding is null && (requirement.Selection == RequirementSelectionMode.Scoped ||
                                    requirement.Selection == RequirementSelectionMode.Admin && candidates.Length > 1))
                return Failure(DependencyResolutionState.PendingBinding, $"binding-required:{requirement.RequirementId}", candidates: candidateSets);

            var selected = Select(requirement, candidates, binding is null);
            resolvedCapabilities.AddRange(selected.Select(value => new ResolvedCapabilityDependency(
                requirement.RequirementId,
                value.ModuleId,
                value.ModuleVersion,
                value.BundleDigest,
                value.CapabilityId,
                value.Version,
                value.LogicalInstance,
                value.InstanceId,
                binding is null ? 0 : request.Bindings.Revision)));
        }

        return new DependencyResolutionResult(
            DependencyResolutionState.Resolved,
            "resolved",
            resolvedModules,
            resolvedCapabilities,
            fallbacks,
            candidateSets,
            []);
    }

    private static CapabilityProvider[] CompatibleProviders(CapabilityRequirement requirement, IReadOnlyList<CapabilityProvider> providers) => providers
        .Where(value => MatchesIdentity(requirement, value.CapabilityId.Value, value.Category) &&
                        requirement.VersionRange.Satisfies(value.Version) &&
                        (requirement.Scope is null || value.Scopes.Contains(requirement.Scope.Value)) &&
                        QualifiersMatch(requirement.Qualifiers, value.Qualifiers))
        .OrderByDescending(value => value.Version)
        .ThenBy(value => value.ModuleId.Value, StringComparer.Ordinal)
        .ThenByDescending(value => value.ModuleVersion)
        .ThenBy(value => value.LogicalInstance, StringComparer.Ordinal)
        .ThenBy(value => value.InstanceId.Value, StringComparer.Ordinal)
        .ToArray();

    private static CapabilityProvider[] Select(CapabilityRequirement requirement, CapabilityProvider[] candidates, bool unbound)
    {
        if (!unbound) return [candidates[0]];
        return requirement.Cardinality is RequirementCardinality.OneOrMany or RequirementCardinality.ZeroOrMany or RequirementCardinality.AllMatching ||
               requirement.Selection == RequirementSelectionMode.ConsumerPolicy
            ? candidates
            : [candidates[0]];
    }

    private static ModuleBinding? FindBinding(DependencyResolutionRequest request, CapabilityRequirement requirement)
    {
        var applicable = request.Bindings.Bindings.Where(value =>
            value.ConsumerModule == request.Consumer.Id &&
            string.Equals(value.RequirementId, requirement.RequirementId, StringComparison.Ordinal)).ToArray();
        if (applicable.Length == 0) return null;
        for (var index = 0; index < request.ScopeContext.Scopes.Count; index++)
        {
            var scope = request.ScopeContext.Scopes[index];
            var exact = applicable.SingleOrDefault(value => value.Scope == scope);
            if (exact is not null) return exact;
            if (index == 0 && !request.Bindings.Policies.InheritParentScopes) break;
        }
        return null;
    }

    private static bool HasInvocationPermission(ModuleManifest consumer, CapabilityRequirement requirement)
    {
        if (!consumer.RequestedPermissions.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("invoke", out var invoke) || invoke.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in invoke.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && requirement.CapabilityId?.Value == item.GetString()) return true;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("category", out var category) && requirement.Category == category.GetString()) return true;
        }
        return false;
    }

    private static bool ConditionMatches(CapabilityRequirement requirement, IReadOnlyDictionary<string, JsonElement> configuration) =>
        requirement.Condition is null || configuration.TryGetValue(requirement.Condition.ConfigurationPath, out var actual) &&
        JsonElement.DeepEquals(actual, requirement.Condition.ExpectedValue);

    private static bool UseFallback(CapabilityRequirement requirement, Dictionary<string, string> fallbacks)
    {
        if (!requirement.IsOptional || requirement.Fallback is null) return false;
        fallbacks[requirement.RequirementId] = requirement.Fallback;
        return true;
    }

    private static bool AllowsZero(RequirementCardinality cardinality) => cardinality is RequirementCardinality.ZeroOrOne or RequirementCardinality.ZeroOrMany or RequirementCardinality.AllMatching;

    private static bool Matches(CapabilityProvider provider, ProviderReference reference) =>
        provider.ModuleId == reference.ModuleId && provider.CapabilityId == reference.CapabilityId &&
        string.Equals(provider.LogicalInstance, reference.Instance, StringComparison.Ordinal);

    private static bool MatchesIdentity(CapabilityRequirement requirement, string capability, string category) =>
        requirement.CapabilityId is { } exact
            ? string.Equals(exact.Value, capability, StringComparison.Ordinal)
            : string.Equals(requirement.Category, category, StringComparison.Ordinal);

    private static bool QualifiersMatch(IReadOnlyDictionary<string, JsonElement> required, IReadOnlyDictionary<string, JsonElement> provided) =>
        required.All(pair => provided.TryGetValue(pair.Key, out var value) && JsonElement.DeepEquals(pair.Value, value));

    private static ProvidedCapability[] DeclaredCandidates(CapabilityRequirement requirement, IReadOnlyList<AvailableModule> modules) => modules
        .SelectMany(value => value.Manifest.Capabilities)
        .Where(value => MatchesIdentity(requirement, value.Id.Value, value.Category) && QualifiersMatch(requirement.Qualifiers, value.Qualifiers))
        .ToArray();

    private static string? FindConflict(ModuleManifest consumer, IReadOnlyList<AvailableModule> modules)
    {
        foreach (var module in modules.Where(value => value.IsActive))
        {
            if (consumer.ConflictingModules.Any(value => value.ModuleId == module.Manifest.Id && value.VersionRange.Satisfies(module.Manifest.Version)) ||
                module.Manifest.ConflictingModules.Any(value => value.ModuleId == consumer.Id && value.VersionRange.Satisfies(consumer.Version)))
                return module.Manifest.Id.Value;
        }
        return null;
    }

    private static IReadOnlyList<string> FindCycle(ModuleManifest consumer, IReadOnlyList<AvailableModule> modules)
    {
        var manifests = modules.Select(value => value.Manifest).Append(consumer)
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(value => value.Version).First());
        var graph = manifests.Values.ToDictionary(value => value.Id, value => Edges(value, manifests.Values).Distinct().ToArray());
        var visiting = new HashSet<Murchalka.ModuleProtocol.Contracts.ModuleId>();
        var visited = new HashSet<Murchalka.ModuleProtocol.Contracts.ModuleId>();
        var path = new List<Murchalka.ModuleProtocol.Contracts.ModuleId>();
        return Visit(consumer.Id) ?? [];

        IReadOnlyList<string>? Visit(Murchalka.ModuleProtocol.Contracts.ModuleId moduleId)
        {
            if (visiting.Contains(moduleId))
            {
                var start = path.FindIndex(value => value == moduleId);
                return path.Skip(start).Append(moduleId).Select(value => value.Value).ToArray();
            }
            if (!visited.Add(moduleId) || !graph.TryGetValue(moduleId, out var edges)) return null;
            visiting.Add(moduleId);
            path.Add(moduleId);
            foreach (var edge in edges)
                if (Visit(edge) is { } cycle) return cycle;
            path.RemoveAt(path.Count - 1);
            visiting.Remove(moduleId);
            return null;
        }
    }

    private static IEnumerable<Murchalka.ModuleProtocol.Contracts.ModuleId> Edges(ModuleManifest manifest, IEnumerable<ModuleManifest> manifests)
    {
        foreach (var requirement in manifest.ModuleRequirements)
            foreach (var candidate in manifests.Where(value => value.Id == requirement.ModuleId && requirement.VersionRange.Satisfies(value.Version)))
                yield return candidate.Id;
        foreach (var requirement in manifest.CapabilityRequirements.Where(value => value.Condition is null))
            foreach (var candidate in manifests.Where(value => value.Capabilities.Any(capability =>
                         MatchesIdentity(requirement, capability.Id.Value, capability.Category) &&
                         requirement.VersionRange.Satisfies(capability.Version) &&
                         QualifiersMatch(requirement.Qualifiers, capability.Qualifiers))))
                yield return candidate.Id;
    }

    private static DependencyResolutionResult Failure(
        DependencyResolutionState state,
        string reason,
        IReadOnlyList<string>? cycle = null,
        IReadOnlyList<DependencyCandidateSet>? candidates = null) =>
        new(state, reason, [], [], new Dictionary<string, string>(StringComparer.Ordinal), candidates ?? [], cycle ?? []);
}
