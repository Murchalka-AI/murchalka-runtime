using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Tests.Infrastructure;

internal static class Phase3TestModuleFactory
{
    public static ModuleManifest Create(
        string id,
        IReadOnlyList<string>? pipelineDefinitions = null,
        IReadOnlyList<PipelineContribution>? pipelineContributions = null,
        IReadOnlyList<EventPublication>? eventPublications = null,
        IReadOnlyList<EventSubscription>? eventSubscriptions = null,
        JsonElement? permissions = null)
    {
        var document = JsonSerializer.SerializeToElement(new { id });
        return new ModuleManifest(
            new ModuleId(id),
            id,
            new SemanticVersion(1, 0, 0),
            "dev.murchalka.tests",
            "*",
            1,
            [],
            [],
            [],
            [],
            [],
            [],
            pipelineDefinitions ?? [],
            pipelineContributions ?? [],
            eventPublications ?? [],
            eventSubscriptions ?? [],
            permissions ?? JsonSerializer.SerializeToElement(new { }),
            new HealthPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 1),
            new ActivationPolicy("automaticWhenTrusted", "keepInactive", true, TimeSpan.FromSeconds(5)),
            document);
    }

    public static void WritePipelineDefinition(string directory, string mode = "sequential")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "input.schema.json"), "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}");
        File.WriteAllText(Path.Combine(directory, "output.schema.json"), "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\"}");
        File.WriteAllText(Path.Combine(directory, "agent.context.pipeline.json"), $$"""
        {
          "apiVersion": "pipelines.murchalka.dev/v1",
          "kind": "PipelineDefinition",
          "metadata": { "id": "agent.context", "version": 1 },
          "input": { "schema": "input.schema.json" },
          "output": { "schema": "output.schema.json" },
          "stages": [{ "id": "enrich", "mode": "{{mode}}" }],
          "semantics": { "deadline": "5s", "cancellation": "required", "checkpointing": "optional" }
        }
        """);
    }
}
