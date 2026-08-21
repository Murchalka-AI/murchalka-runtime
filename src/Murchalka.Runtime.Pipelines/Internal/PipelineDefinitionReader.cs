using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Pipelines;

namespace Murchalka.Runtime.Pipelines.Internal;

internal static partial class PipelineDefinitionReader
{
    public static IReadOnlyList<PipelineDefinition> ReadAll(ModuleManifest manifest, string contentPath)
    {
        var definitions = manifest.PipelineDefinitionPaths.Select(path => Read(manifest, ResolveInside(contentPath, path))).ToArray();
        var duplicate = definitions.GroupBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Pipeline definition '{duplicate.Key}' is declared more than once by module '{manifest.Id}'.");
        return definitions;
    }

    private static PipelineDefinition Read(ModuleManifest manifest, string definitionPath)
    {
        if (!File.Exists(definitionPath)) throw new InvalidDataException($"Pipeline definition '{definitionPath}' is missing.");
        var root = StructuredDocument.Load(definitionPath).AsObject();
        RequireKeys(root, ["apiVersion", "kind", "metadata", "input", "output", "stages", "semantics"]);
        if (RequiredString(root, "apiVersion") != "pipelines.murchalka.dev/v1" || RequiredString(root, "kind") != "PipelineDefinition")
            throw new InvalidDataException($"Pipeline definition '{definitionPath}' has an unsupported kind or API version.");
        var metadata = RequiredObject(root, "metadata");
        RequireKeys(metadata, ["id", "version"]);
        var id = RequiredString(metadata, "id");
        _ = new CapabilityId(id);
        var version = metadata["version"]?.GetValue<int>() ?? throw new InvalidDataException("Pipeline metadata.version is missing.");
        if (version < 1) throw new InvalidDataException("Pipeline metadata.version must be positive.");
        var input = RequiredObject(root, "input");
        var output = RequiredObject(root, "output");
        RequireKeys(input, ["schema"]);
        RequireKeys(output, ["schema"]);
        var directory = Path.GetDirectoryName(definitionPath)!;
        var inputPath = ResolveInside(directory, RequiredString(input, "schema"));
        var outputPath = ResolveInside(directory, RequiredString(output, "schema"));
        EnsureSchema(inputPath);
        EnsureSchema(outputPath);
        var stages = RequiredArray(root, "stages").Select(value => ReadStage(value!.AsObject())).ToArray();
        if (stages.Length == 0) throw new InvalidDataException($"Pipeline '{id}' must define at least one stage.");
        var duplicateStage = stages.GroupBy(value => value.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateStage is not null) throw new InvalidDataException($"Pipeline '{id}' contains duplicate stage '{duplicateStage.Key}'.");
        var semantics = RequiredObject(root, "semantics");
        RequireKeys(semantics, ["deadline", "cancellation", "checkpointing"]);
        var cancellation = RequiredString(semantics, "cancellation");
        if (cancellation is not ("required" or "optional")) throw new InvalidDataException($"Pipeline '{id}' has invalid cancellation semantics.");
        var checkpointing = RequiredString(semantics, "checkpointing");
        if (checkpointing is not ("required" or "optional" or "disabled")) throw new InvalidDataException($"Pipeline '{id}' has invalid checkpointing semantics.");
        return new PipelineDefinition(
            id,
            version,
            manifest.Id,
            manifest.Version,
            inputPath,
            outputPath,
            Digest(inputPath),
            Digest(outputPath),
            stages,
            ParseDuration(RequiredString(semantics, "deadline")),
            cancellation == "required",
            checkpointing);
    }

    private static PipelineStageDefinition ReadStage(System.Text.Json.Nodes.JsonObject value)
    {
        RequireKeys(value, ["id", "mode"]);
        var id = RequiredString(value, "id");
        if (!LocalId().IsMatch(id)) throw new InvalidDataException($"Pipeline stage id '{id}' is invalid.");
        return new PipelineStageDefinition(id, RequiredString(value, "mode") switch
        {
            "sequential" => PipelineStageMode.Sequential,
            "parallelMerge" => PipelineStageMode.ParallelMerge,
            "firstSuccessful" => PipelineStageMode.FirstSuccessful,
            "exactlyOne" => PipelineStageMode.ExactlyOne,
            "fanOut" => PipelineStageMode.FanOut,
            "reduce" => PipelineStageMode.Reduce,
            var mode => throw new InvalidDataException($"Unknown pipeline stage mode '{mode}'.")
        });
    }

    private static string ResolveInside(string root, string relative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Pipeline path '{relative}' escapes its owning directory.");
        return path;
    }

    private static void EnsureSchema(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"Pipeline schema '{path}' is missing.");
        _ = Json.Schema.JsonSchema.FromText(File.ReadAllText(path));
    }

    private static string Digest(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static System.Text.Json.Nodes.JsonObject RequiredObject(System.Text.Json.Nodes.JsonObject value, string name) => value[name]?.AsObject() ?? throw new InvalidDataException($"Pipeline object '{name}' is missing.");
    private static System.Text.Json.Nodes.JsonArray RequiredArray(System.Text.Json.Nodes.JsonObject value, string name) => value[name]?.AsArray() ?? throw new InvalidDataException($"Pipeline array '{name}' is missing.");
    private static string RequiredString(System.Text.Json.Nodes.JsonObject value, string name) => value[name]?.GetValue<string>() ?? throw new InvalidDataException($"Pipeline value '{name}' is missing.");

    private static void RequireKeys(System.Text.Json.Nodes.JsonObject value, IReadOnlyCollection<string> allowed)
    {
        var unknown = value.Select(pair => pair.Key).FirstOrDefault(key => !allowed.Contains(key));
        if (unknown is not null) throw new InvalidDataException($"Pipeline document property '{unknown}' is not supported.");
        var missing = allowed.FirstOrDefault(key => !value.ContainsKey(key));
        if (missing is not null) throw new InvalidDataException($"Pipeline document property '{missing}' is required.");
    }

    private static TimeSpan ParseDuration(string value)
    {
        var unitLength = value.EndsWith("ms", StringComparison.Ordinal) ? 2 : 1;
        if (value.Length <= unitLength || !double.TryParse(value[..^unitLength], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new InvalidDataException($"Pipeline duration '{value}' is invalid.");
        return value[^unitLength..] switch
        {
            "ms" => TimeSpan.FromMilliseconds(number),
            "s" => TimeSpan.FromSeconds(number),
            "m" => TimeSpan.FromMinutes(number),
            "h" => TimeSpan.FromHours(number),
            _ => throw new InvalidDataException($"Pipeline duration '{value}' has an unsupported unit.")
        };
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LocalId();
}
