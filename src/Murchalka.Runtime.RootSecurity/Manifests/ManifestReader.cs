using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.RootSecurity.Manifests;

/// <summary>Converts a schema-validated manifest document into runtime domain models.</summary>
public static class ManifestReader
{
    /// <summary>Reads a module manifest from a structured document.</summary>
    /// <param name="document">The JSON-compatible manifest document.</param>
    /// <returns>The parsed module manifest.</returns>
    public static ModuleManifest Read(JsonNode document)
    {
        var root = document.AsObject();
        var metadata = RequiredObject(root, "metadata");
        var compatibility = RequiredObject(root, "compatibility");
        var artifacts = RequiredObject(root, "artifacts");
        var provides = RequiredObject(root, "provides");
        var health = RequiredObject(root, "health");
        var activation = RequiredObject(root, "activation");
        var protocol = int.Parse(RequiredString(compatibility, "moduleProtocol"), CultureInfo.InvariantCulture);
        var runtimeArtifacts = artifacts["runtime"]?.AsArray().Select(item =>
        {
            var value = item!.AsObject();
            return new RuntimeArtifact(
                RequiredString(value, "id"), RequiredString(value, "mode"),
                ReadSet(value["os"]), ReadSet(value["architectures"]),
                RequiredString(value, "entrypoint"), RequiredString(value, "digest"),
                value["protocolVersion"]?.GetValue<int>() ?? protocol);
        }).ToArray() ?? [];
        var capabilities = provides["capabilities"]?.AsArray().Select(item =>
        {
            var value = item!.AsObject();
            var execution = RequiredObject(value, "execution");
            return new ProvidedCapability(new CapabilityId(RequiredString(value, "id")), RequiredString(value, "category"),
                SemanticVersion.Parse(RequiredString(value, "version")), RequiredString(value, "contract"),
                ParseDuration(RequiredString(execution, "timeout")), ReadValues(value["qualifiers"]), ReadScopes(value["scope"]));
        }).ToArray() ?? [];
        var required = root["requires"]?.AsObject();
        var moduleRequirements = ReadModuleRequirements(required?["modules"]);
        var capabilityRequirements = ReadCapabilityRequirements(required?["capabilities"], isOptional: false);
        var optionalRequirements = ReadCapabilityRequirements(root["optional"]?["capabilities"], isOptional: true);
        var conflicts = ReadModuleRequirements(root["conflicts"]?["modules"]);
        var contributions = root["contributes"]?.AsObject();
        var pipelineContributions = ReadPipelineContributions(contributions?["pipelines"]);
        var events = contributions?["events"]?.AsObject();
        var eventPublications = ReadEventPublications(events?["publications"]);
        var eventSubscriptions = ReadEventSubscriptions(events?["subscriptions"]);
        var pipelineDefinitionPaths = ReadPipelineDefinitionPaths(root["extensions"]);
        RejectContributionDuplicates(pipelineDefinitionPaths, pipelineContributions, eventPublications, eventSubscriptions);
        var configuration = ReadConfiguration(root["configuration"]);
        var storageNamespaces = ReadStorageNamespaces(root["storage"]);
        var upgrade = ReadUpgradePolicy(root["upgrade"]);
        var readiness = RequiredObject(health, "readiness");
        var permissions = root["permissions"] ?? new JsonObject();
        return new ModuleManifest(
            new ModuleId(RequiredString(metadata, "id")), RequiredString(metadata, "name"), SemanticVersion.Parse(RequiredString(metadata, "version")),
            RequiredString(metadata, "publisher"), compatibility["runtime"]?.GetValue<string>() ?? "*", protocol,
            runtimeArtifacts, capabilities, moduleRequirements, capabilityRequirements, optionalRequirements, conflicts,
            pipelineDefinitionPaths, pipelineContributions, eventPublications, eventSubscriptions,
            configuration, storageNamespaces,
            JsonSerializer.SerializeToElement(permissions),
            new HealthPolicy(ParseDuration(RequiredString(health, "startupTimeout")), ParseDuration(RequiredString(readiness, "timeout")), readiness["failureThreshold"]!.GetValue<int>()),
            new ActivationPolicy(RequiredString(activation, "mode"), RequiredString(activation, "failurePolicy"), activation["hotReload"]!.GetValue<bool>(), ParseDuration(RequiredString(activation, "drainTimeout"))),
            upgrade,
            JsonSerializer.SerializeToElement(root));
    }

    private static JsonObject RequiredObject(JsonObject value, string name) => value[name]?.AsObject() ?? throw new InvalidDataException($"Manifest object '{name}' is missing.");
    private static string RequiredString(JsonObject value, string name) => value[name]?.GetValue<string>() ?? throw new InvalidDataException($"Manifest value '{name}' is missing.");
    private static HashSet<string> ReadSet(JsonNode? node) => node is JsonArray array ? array.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) : [];

    private static Dictionary<string, JsonElement> ReadValues(JsonNode? node) => node is JsonObject value
        ? value.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal)
        : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static HashSet<BindingScopeType> ReadScopes(JsonNode? node) => node is JsonArray array
        ? array.Select(item => ParseScope(item!.GetValue<string>())).ToHashSet()
        : Enum.GetValues<BindingScopeType>().ToHashSet();

    private static ModuleRequirement[] ReadModuleRequirements(JsonNode? node) => node is JsonArray array
        ? array.Select(item =>
        {
            var value = item!.AsObject();
            return new ModuleRequirement(
                new ModuleId(RequiredString(value, "id")),
                VersionRangeExpression.Parse(RequiredString(value, "version")),
                value["reason"]?.GetValue<string>());
        }).ToArray()
        : [];

    private static CapabilityRequirement[] ReadCapabilityRequirements(JsonNode? node, bool isOptional) => node is JsonArray array
        ? array.Select(item =>
        {
            var value = item!.AsObject();
            var capability = value["capability"]?.GetValue<string>();
            var category = value["category"]?.GetValue<string>();
            var cardinality = ParseCardinality(RequiredString(value, "cardinality"));
            var selection = value["selection"]?.GetValue<string>() is { } selectionText
                ? ParseSelection(selectionText)
                : RequirementSelectionMode.Admin;
            RequirementCondition? condition = null;
            if (value["when"] is JsonObject when)
                condition = new RequirementCondition(RequiredString(when, "configuration"), JsonSerializer.SerializeToElement(when["equals"]));
            return new CapabilityRequirement(
                RequiredString(value, "requirementId"),
                capability is null ? null : new CapabilityId(capability),
                category,
                VersionRangeExpression.Parse(value["version"]?.GetValue<string>() ?? "*"),
                ReadValues(value["qualifiers"]),
                cardinality,
                selection,
                value["scope"]?.GetValue<string>() is { } scope ? ParseScope(scope) : null,
                value["fallback"]?.GetValue<string>(),
                condition,
                isOptional);
        }).ToArray()
        : [];

    private static PipelineContribution[] ReadPipelineContributions(JsonNode? node) => node is JsonArray array
        ? array.Select(item =>
        {
            var value = item!.AsObject();
            var order = value["order"]?.AsObject();
            return new PipelineContribution(
                RequiredString(value, "pipeline"),
                RequiredString(value, "stage"),
                RequiredString(value, "handler"),
                ReadSet(order?["after"]),
                ReadSet(order?["before"]),
                ParsePipelineFailureMode(RequiredString(value, "failureMode")),
                ParseDuration(RequiredString(value, "timeout")));
        }).ToArray()
        : [];

    private static EventPublication[] ReadEventPublications(JsonNode? node) => node is JsonArray array
        ? array.Select(item =>
        {
            var value = item!.AsObject();
            return new EventPublication(RequiredString(value, "topic"), RequiredString(value, "schema"));
        }).ToArray()
        : [];

    private static EventSubscription[] ReadEventSubscriptions(JsonNode? node) => node is JsonArray array
        ? array.Select(item =>
        {
            var value = item!.AsObject();
            return new EventSubscription(RequiredString(value, "topic"), RequiredString(value, "schema"), RequiredString(value, "handler"));
        }).ToArray()
        : [];

    private static string[] ReadPipelineDefinitionPaths(JsonNode? node)
    {
        if (node is not JsonObject extensions ||
            extensions["dev.murchalka.pipelines"] is not JsonObject pipelineExtension ||
            pipelineExtension["definitions"] is not JsonArray definitions)
            return [];
        if (pipelineExtension.Any(pair => pair.Key != "definitions"))
            throw new InvalidDataException("The dev.murchalka.pipelines extension contains an unsupported property.");
        return definitions.Select(item => item?.GetValue<string>() ?? throw new InvalidDataException("Pipeline definition path cannot be null.")).ToArray();
    }

    private static ConfigurationDeclaration? ReadConfiguration(JsonNode? node)
    {
        if (node is not JsonObject value) return null;
        return new ConfigurationDeclaration(
            RequiredString(value, "schema"),
            value["defaults"]?.GetValue<string>(),
            RequiredString(value, "restartPolicy") switch
            {
                "reload" => ConfigurationRestartPolicy.Reload,
                "restartModule" => ConfigurationRestartPolicy.RestartModule,
                "restartTarget" => ConfigurationRestartPolicy.RestartTarget,
                "immutable" => ConfigurationRestartPolicy.Immutable,
                var policy => throw new InvalidDataException($"Unknown configuration restart policy '{policy}'.")
            });
    }

    private static StorageNamespaceDeclaration[] ReadStorageNamespaces(JsonNode? node)
    {
        if (node is not JsonObject storage || storage["namespaces"] is not JsonArray namespaces) return [];
        var declarations = namespaces.Select(item =>
        {
            var value = item!.AsObject();
            return new StorageNamespaceDeclaration(
                RequiredString(value, "name"),
                RequiredString(value, "requirement"),
                RequiredString(value, "migrations"),
                ParseDataClassification(RequiredString(value, "dataClass")),
                value["exportable"]!.GetValue<bool>(),
                RequiredString(value, "purgeMode") switch
                {
                    "explicit" => StoragePurgeMode.Explicit,
                    "onUninstallWithApproval" => StoragePurgeMode.OnUninstallWithApproval,
                    "retain" => StoragePurgeMode.Retain,
                    var mode => throw new InvalidDataException($"Unknown storage purge mode '{mode}'.")
                });
        }).ToArray();
        var duplicate = declarations.GroupBy(value => value.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Storage namespace '{duplicate.Key}' is duplicated.");
        return declarations;
    }

    private static ModuleUpgradePolicy? ReadUpgradePolicy(JsonNode? node)
    {
        if (node is not JsonObject value) return null;
        if (!string.Equals(RequiredString(value, "strategy"), "sideBySide", StringComparison.Ordinal))
            throw new InvalidDataException("Only side-by-side module upgrades are supported.");
        return new ModuleUpgradePolicy(
            ParseDuration(RequiredString(value, "rollbackWindow")),
            RequiredString(value, "stateMigration") switch
            {
                "none" => StateMigrationRequirement.None,
                "optional" => StateMigrationRequirement.Optional,
                "required" => StateMigrationRequirement.Required,
                var requirement => throw new InvalidDataException($"Unknown state migration requirement '{requirement}'.")
            });
    }

    private static DataClassification ParseDataClassification(string value) => value switch
    {
        "public" => DataClassification.Public,
        "internal" => DataClassification.Internal,
        "personal" => DataClassification.Personal,
        "sensitive" => DataClassification.Sensitive,
        "restricted" => DataClassification.Restricted,
        _ => throw new InvalidDataException($"Unknown data classification '{value}'.")
    };

    private static void RejectContributionDuplicates(
        IReadOnlyList<string> definitionPaths,
        IReadOnlyList<PipelineContribution> pipelineContributions,
        IReadOnlyList<EventPublication> eventPublications,
        IReadOnlyList<EventSubscription> eventSubscriptions)
    {
        var definition = definitionPaths.GroupBy(value => value, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (definition is not null) throw new InvalidDataException($"Pipeline definition path '{definition.Key}' is duplicated.");
        var pipeline = pipelineContributions.GroupBy(value => (value.PipelineId, value.StageId, value.HandlerId)).FirstOrDefault(group => group.Count() > 1);
        if (pipeline is not null) throw new InvalidDataException($"Pipeline handler '{pipeline.Key.HandlerId}' is duplicated in '{pipeline.Key.PipelineId}/{pipeline.Key.StageId}'.");
        var publication = eventPublications.GroupBy(value => value.Topic, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (publication is not null) throw new InvalidDataException($"Event publication '{publication.Key}' is duplicated.");
        var subscription = eventSubscriptions.GroupBy(value => (value.Topic, value.HandlerId)).FirstOrDefault(group => group.Count() > 1);
        if (subscription is not null) throw new InvalidDataException($"Event subscription '{subscription.Key.Topic}/{subscription.Key.HandlerId}' is duplicated.");
    }

    private static BindingScopeType ParseScope(string value) => value switch
    {
        "global" => BindingScopeType.Global,
        "tenant" => BindingScopeType.Tenant,
        "workspace" => BindingScopeType.Workspace,
        "person" => BindingScopeType.Person,
        "group" => BindingScopeType.Group,
        "module" => BindingScopeType.Module,
        "node" => BindingScopeType.Node,
        "session" => BindingScopeType.Session,
        _ => throw new InvalidDataException($"Unknown binding scope '{value}'.")
    };

    private static RequirementCardinality ParseCardinality(string value) => value switch
    {
        "exactlyOne" => RequirementCardinality.ExactlyOne,
        "zeroOrOne" => RequirementCardinality.ZeroOrOne,
        "oneOrMany" => RequirementCardinality.OneOrMany,
        "zeroOrMany" => RequirementCardinality.ZeroOrMany,
        "allMatching" => RequirementCardinality.AllMatching,
        _ => throw new InvalidDataException($"Unknown requirement cardinality '{value}'.")
    };

    private static RequirementSelectionMode ParseSelection(string value) => value switch
    {
        "admin" => RequirementSelectionMode.Admin,
        "automatic" => RequirementSelectionMode.Automatic,
        "preferred" => RequirementSelectionMode.Preferred,
        "consumerPolicy" => RequirementSelectionMode.ConsumerPolicy,
        "scoped" => RequirementSelectionMode.Scoped,
        _ => throw new InvalidDataException($"Unknown requirement selection mode '{value}'.")
    };

    private static PipelineFailureMode ParsePipelineFailureMode(string value) => value switch
    {
        "fail" => PipelineFailureMode.Fail,
        "continue" => PipelineFailureMode.Continue,
        "fallback" => PipelineFailureMode.Fallback,
        _ => throw new InvalidDataException($"Unknown pipeline failure mode '{value}'.")
    };

    internal static TimeSpan ParseDuration(string value)
    {
        if (value.Length < 2) throw new FormatException($"Invalid duration '{value}'.");
        var unitLength = value.EndsWith("ms", StringComparison.Ordinal) ? 2 : 1;
        if (!double.TryParse(value[..^unitLength], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) throw new FormatException($"Invalid duration '{value}'.");
        return value[^unitLength..] switch
        {
            "ms" => TimeSpan.FromMilliseconds(number),
            "s" => TimeSpan.FromSeconds(number),
            "m" => TimeSpan.FromMinutes(number),
            "h" => TimeSpan.FromHours(number),
            "d" => TimeSpan.FromDays(number),
            _ => throw new FormatException($"Invalid duration unit in '{value}'.")
        };
    }
}
