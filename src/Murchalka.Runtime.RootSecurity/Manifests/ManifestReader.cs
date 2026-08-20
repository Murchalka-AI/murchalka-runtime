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
        var readiness = RequiredObject(health, "readiness");
        var permissions = root["permissions"] ?? new JsonObject();
        return new ModuleManifest(
            new ModuleId(RequiredString(metadata, "id")), RequiredString(metadata, "name"), SemanticVersion.Parse(RequiredString(metadata, "version")),
            RequiredString(metadata, "publisher"), compatibility["runtime"]?.GetValue<string>() ?? "*", protocol,
            runtimeArtifacts, capabilities, moduleRequirements, capabilityRequirements, optionalRequirements, conflicts,
            JsonSerializer.SerializeToElement(permissions),
            new HealthPolicy(ParseDuration(RequiredString(health, "startupTimeout")), ParseDuration(RequiredString(readiness, "timeout")), readiness["failureThreshold"]!.GetValue<int>()),
            new ActivationPolicy(RequiredString(activation, "mode"), RequiredString(activation, "failurePolicy"), activation["hotReload"]!.GetValue<bool>(), ParseDuration(RequiredString(activation, "drainTimeout"))),
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
