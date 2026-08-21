using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Dependencies;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Dependencies.Resolution;

namespace Murchalka.Runtime.Tests.Dependencies;

/// <summary>Verifies deterministic resolver properties across candidate orderings and graph shapes.</summary>
public sealed class DependencyResolverPropertyTests
{
    private static readonly JsonElement Persistent = JsonSerializer.SerializeToElement("persistent");
    private static readonly string[] ExactCapabilityPermission = ["storage.records"];

    /// <summary>Verifies that install order never changes ambiguity or candidate comparison order.</summary>
    [Fact]
    public void AdminSelectionIsInvariantAcrossEveryProviderPermutation()
    {
        var consumer = Consumer(RequirementSelectionMode.Admin);
        var providers = new[]
        {
            Provider("dev.murchalka.storage-zeta", "2.1.0"),
            Provider("dev.murchalka.storage-alpha", "2.2.0"),
            Provider("dev.murchalka.storage-beta", "2.2.0")
        };
        var resolver = new DependencyResolver();

        foreach (var permutation in Permutations(providers))
        {
            var result = resolver.Resolve(Request(consumer, permutation, BindingDocument.Empty("local")));

            Assert.Equal(DependencyResolutionState.PendingBinding, result.State);
            Assert.Equal(
                ["dev.murchalka.storage-alpha", "dev.murchalka.storage-beta", "dev.murchalka.storage-zeta"],
                Assert.Single(result.CandidateSets).Candidates.Select(value => value.ModuleId.Value));
        }
    }

    /// <summary>Verifies scoped administrator selection, qualifier filtering, and binding revision capture.</summary>
    [Fact]
    public void ScopedBindingSelectsOnlyTheCompatibleNamedProvider()
    {
        var consumer = Consumer(RequirementSelectionMode.Scoped);
        var selected = Provider("dev.murchalka.storage-postgresql", "2.2.0");
        var rejected = Provider("dev.murchalka.storage-memory", "2.3.0", durability: "ephemeral");
        var bindings = new BindingDocument("local", 42,
        [
            new ModuleBinding("storage-choice", consumer.Id, "records", BindingScope.Global,
                new ProviderSelection(new ProviderReference(selected.ModuleId, selected.CapabilityId, "default"), [], new HashSet<string>(), 0))
        ], new BindingPolicies(false, false));
        var resolver = new DependencyResolver();

        var result = resolver.Resolve(Request(consumer, [rejected, selected], bindings));

        Assert.True(result.Succeeded);
        var dependency = Assert.Single(result.CapabilityDependencies);
        Assert.Equal(selected.ModuleId, dependency.ProviderModule);
        Assert.Equal(42, dependency.BindingRevision);
    }

    /// <summary>Verifies exact-capability matching independently from a shared category.</summary>
    [Fact]
    public void ExactCapabilityDoesNotSelectAnotherCapabilityInTheSameCategory()
    {
        var exact = Provider("dev.murchalka.storage-records", "2.2.0");
        var other = Provider("dev.murchalka.storage-archive", "2.3.0") with { CapabilityId = new CapabilityId("storage.archive") };
        var requirement = new CapabilityRequirement("records", new CapabilityId("storage.records"), null, VersionRangeExpression.Parse("2"),
            new Dictionary<string, JsonElement>(), RequirementCardinality.ExactlyOne, RequirementSelectionMode.Automatic, null, null, null, false);
        var permissions = JsonSerializer.SerializeToElement(new { capabilities = new { invoke = ExactCapabilityPermission } });
        var consumer = Module("dev.murchalka.exact-consumer", capabilityRequirements: [requirement], permissions: permissions);
        var resolver = new DependencyResolver();

        var result = resolver.Resolve(Request(consumer, [other, exact], BindingDocument.Empty("local")));

        Assert.True(result.Succeeded);
        Assert.Equal(exact.ModuleId, Assert.Single(result.CapabilityDependencies).ProviderModule);
    }

    /// <summary>Verifies plural cardinality returns every matching provider in deterministic order.</summary>
    [Fact]
    public void OneOrManySelectsEveryCompatibleProviderDeterministically()
    {
        var requirement = new CapabilityRequirement("records", null, "storage.records", VersionRangeExpression.Parse("2"),
            new Dictionary<string, JsonElement>(), RequirementCardinality.OneOrMany, RequirementSelectionMode.Automatic, null, null, null, false);
        var permissions = JsonSerializer.SerializeToElement(new { capabilities = new { invoke = new[] { new { category = "storage.records" } } } });
        var consumer = Module("dev.murchalka.fanout-consumer", capabilityRequirements: [requirement], permissions: permissions);
        var resolver = new DependencyResolver();

        var result = resolver.Resolve(Request(consumer,
            [Provider("dev.murchalka.storage-zeta", "2.1.0"), Provider("dev.murchalka.storage-alpha", "2.2.0")], BindingDocument.Empty("local")));

        Assert.True(result.Succeeded);
        Assert.Equal(["dev.murchalka.storage-alpha", "dev.murchalka.storage-zeta"], result.CapabilityDependencies.Select(value => value.ProviderModule.Value));
    }

    /// <summary>Verifies that a complete exact-module cycle path is returned.</summary>
    [Fact]
    public void ExactModuleCycleIsRejectedWithCompletePath()
    {
        var a = Module("dev.murchalka.a", moduleRequirements:
        [
            new ModuleRequirement(new ModuleId("dev.murchalka.b"), VersionRangeExpression.Parse(">=1.0.0 <2.0.0"), null)
        ]);
        var b = Module("dev.murchalka.b", moduleRequirements:
        [
            new ModuleRequirement(a.Id, VersionRangeExpression.Parse("1"), null)
        ]);
        var resolver = new DependencyResolver();

        var result = resolver.Resolve(new DependencyResolutionRequest(
            a, Digest('a'), [new AvailableModule(b, Digest('b'), false)], [], BindingDocument.Empty("local"),
            new BindingScopeContext([BindingScope.Global]), new Dictionary<string, JsonElement>()));

        Assert.Equal(DependencyResolutionState.Conflict, result.State);
        Assert.Equal([a.Id.Value, b.Id.Value, a.Id.Value], result.CyclePath);
    }

    /// <summary>Verifies exact-module SemVer success and incompatible-version diagnostics.</summary>
    [Fact]
    public void ExactModuleRequirementEnforcesSemanticVersionRange()
    {
        var providerId = new ModuleId("dev.murchalka.people");
        var provider = Module(providerId.Value, version: "1.4.0");
        var resolver = new DependencyResolver();
        var compatibleConsumer = Module("dev.murchalka.compatible", moduleRequirements:
        [
            new ModuleRequirement(providerId, VersionRangeExpression.Parse(">=1.0.0 <2.0.0"), null)
        ]);
        var incompatibleConsumer = Module("dev.murchalka.incompatible", moduleRequirements:
        [
            new ModuleRequirement(providerId, VersionRangeExpression.Parse(">=2.0.0 <3.0.0"), null)
        ]);
        var modules = new[] { new AvailableModule(provider, Digest('d'), true) };

        var compatible = resolver.Resolve(new DependencyResolutionRequest(compatibleConsumer, Digest('e'), modules, [],
            BindingDocument.Empty("local"), new BindingScopeContext([BindingScope.Global]), new Dictionary<string, JsonElement>()));
        var incompatible = resolver.Resolve(new DependencyResolutionRequest(incompatibleConsumer, Digest('f'), modules, [],
            BindingDocument.Empty("local"), new BindingScopeContext([BindingScope.Global]), new Dictionary<string, JsonElement>()));

        Assert.True(compatible.Succeeded);
        Assert.Equal(SemanticVersion.Parse("1.4.0"), Assert.Single(compatible.ModuleDependencies).Version);
        Assert.Equal(DependencyResolutionState.Incompatible, incompatible.State);
    }

    /// <summary>Verifies a declared module conflict blocks activation before provider selection.</summary>
    [Fact]
    public void DeclaredModuleConflictFailsClosed()
    {
        var provider = Module("dev.murchalka.legacy", version: "1.5.0");
        var consumer = Module("dev.murchalka.modern", conflicts:
        [
            new ModuleRequirement(provider.Id, VersionRangeExpression.Parse("1"), "Mutually exclusive implementations.")
        ]);
        var resolver = new DependencyResolver();

        var result = resolver.Resolve(new DependencyResolutionRequest(consumer, Digest('e'), [new AvailableModule(provider, Digest('f'), true)], [],
            BindingDocument.Empty("local"), new BindingScopeContext([BindingScope.Global]), new Dictionary<string, JsonElement>()));

        Assert.Equal(DependencyResolutionState.Conflict, result.State);
        Assert.Equal("module-conflict:dev.murchalka.legacy", result.ReasonCode);
    }

    /// <summary>Verifies that an absent optional provider activates its named fallback.</summary>
    [Fact]
    public void OptionalRequirementUsesNamedFallback()
    {
        var optional = new CapabilityRequirement("relationships", null, "social.relationship-context", VersionRangeExpression.Parse("*"),
            new Dictionary<string, JsonElement>(), RequirementCardinality.ZeroOrOne, RequirementSelectionMode.Admin, null, "neutral-behavior", null, true);
        var consumer = Module("dev.murchalka.optional-consumer", optionalRequirements: [optional]);
        var resolver = new DependencyResolver();

        var result = resolver.Resolve(Request(consumer, [], BindingDocument.Empty("local")));

        Assert.True(result.Succeeded);
        Assert.Equal("neutral-behavior", result.Fallbacks["relationships"]);
    }

    private static DependencyResolutionRequest Request(ModuleManifest consumer, IReadOnlyList<CapabilityProvider> providers, BindingDocument bindings)
    {
        var modules = providers.Select(value => new AvailableModule(Module(value.ModuleId.Value,
            capabilities: [Provided(value.Version, value.Qualifiers)]), value.BundleDigest, true)).ToArray();
        return new DependencyResolutionRequest(consumer, Digest('c'), modules, providers, bindings,
            new BindingScopeContext([BindingScope.Global]), new Dictionary<string, JsonElement>());
    }

    private static ModuleManifest Consumer(RequirementSelectionMode selection)
    {
        var requirement = new CapabilityRequirement("records", null, "storage.records", VersionRangeExpression.Parse(">=2.0.0 <3.0.0"),
            new Dictionary<string, JsonElement> { ["durability"] = Persistent }, RequirementCardinality.ExactlyOne, selection,
            BindingScopeType.Global, null, null, false);
        var permissions = JsonSerializer.SerializeToElement(new { capabilities = new { invoke = new[] { new { category = "storage.records" } } } });
        return Module("dev.murchalka.consumer", capabilityRequirements: [requirement], permissions: permissions);
    }

    private static CapabilityProvider Provider(string moduleId, string version, string durability = "persistent")
    {
        var qualifiers = new Dictionary<string, JsonElement> { ["durability"] = JsonSerializer.SerializeToElement(durability) };
        return new CapabilityProvider(new CapabilityId("storage.records"), SemanticVersion.Parse(version), new ModuleId(moduleId),
            new InstanceId(moduleId.Split('.').Last() + "-runtime"), "storage.records", "/contract.json", TimeSpan.FromSeconds(2),
            "default", SemanticVersion.Parse("1.0.0"), Digest(moduleId[^1]), qualifiers, new HashSet<BindingScopeType> { BindingScopeType.Global });
    }

    private static ProvidedCapability Provided(SemanticVersion version, IReadOnlyDictionary<string, JsonElement> qualifiers) => new(
        new CapabilityId("storage.records"), "storage.records", version, "contract.json", TimeSpan.FromSeconds(2), qualifiers,
        new HashSet<BindingScopeType> { BindingScopeType.Global });

    private static ModuleManifest Module(
        string id,
        string version = "1.0.0",
        IReadOnlyList<ProvidedCapability>? capabilities = null,
        IReadOnlyList<ModuleRequirement>? moduleRequirements = null,
        IReadOnlyList<CapabilityRequirement>? capabilityRequirements = null,
        IReadOnlyList<CapabilityRequirement>? optionalRequirements = null,
        IReadOnlyList<ModuleRequirement>? conflicts = null,
        JsonElement? permissions = null) => new(
            new ModuleId(id), id, SemanticVersion.Parse(version), "dev.murchalka.tests", "*", 1, [], capabilities ?? [],
            moduleRequirements ?? [], capabilityRequirements ?? [], optionalRequirements ?? [], conflicts ?? [], [], [], [], [],
            permissions ?? JsonSerializer.SerializeToElement(new { }),
            new HealthPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 1),
            new ActivationPolicy("automaticWhenTrusted", "keepInactive", true, TimeSpan.FromSeconds(1)), JsonSerializer.SerializeToElement(new { }));

    private static IEnumerable<T[]> Permutations<T>(IReadOnlyList<T> values)
    {
        if (values.Count == 0) { yield return []; yield break; }
        for (var index = 0; index < values.Count; index++)
        {
            var rest = values.Where((_, candidateIndex) => candidateIndex != index).ToArray();
            foreach (var suffix in Permutations(rest)) yield return [values[index], .. suffix];
        }
    }

    private static string Digest(char value) => "sha256:" + new string(value, 64);
}
