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
                ParseDuration(RequiredString(execution, "timeout")));
        }).ToArray() ?? [];
        var readiness = RequiredObject(health, "readiness");
        var permissions = root["permissions"] ?? new JsonObject();
        var hasRequiredDependencies = root["requires"] is JsonObject requires && requires.Any(pair => pair.Value is JsonArray array && array.Count > 0);
        return new ModuleManifest(
            new ModuleId(RequiredString(metadata, "id")), RequiredString(metadata, "name"), SemanticVersion.Parse(RequiredString(metadata, "version")),
            RequiredString(metadata, "publisher"), compatibility["runtime"]?.GetValue<string>() ?? "*", protocol,
            runtimeArtifacts, capabilities, hasRequiredDependencies, JsonSerializer.SerializeToElement(permissions),
            new HealthPolicy(ParseDuration(RequiredString(health, "startupTimeout")), ParseDuration(RequiredString(readiness, "timeout")), readiness["failureThreshold"]!.GetValue<int>()),
            new ActivationPolicy(RequiredString(activation, "mode"), RequiredString(activation, "failurePolicy"), activation["hotReload"]!.GetValue<bool>(), ParseDuration(RequiredString(activation, "drainTimeout"))),
            JsonSerializer.SerializeToElement(root));
    }

    private static JsonObject RequiredObject(JsonObject value, string name) => value[name]?.AsObject() ?? throw new InvalidDataException($"Manifest object '{name}' is missing.");
    private static string RequiredString(JsonObject value, string name) => value[name]?.GetValue<string>() ?? throw new InvalidDataException($"Manifest value '{name}' is missing.");
    private static HashSet<string> ReadSet(JsonNode? node) => node is JsonArray array ? array.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) : [];

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
