using System.Text.Json.Nodes;
using Murchalka.Runtime.RootSecurity.Manifests;

namespace Murchalka.Runtime.Tests.RootSecurity;

/// <summary>Verifies versioned and legacy UI contribution manifest parsing.</summary>
public sealed class ManifestReaderTests
{
    /// <summary>Verifies that distinct versioned properties and events schemas are retained.</summary>
    [Fact]
    public void UiComponentReadsVersionedPropertiesAndEventsSchemas()
    {
        var manifest = ManifestReader.Read(CreateManifest(new JsonObject
        {
            ["id"] = "client.proof.card",
            ["version"] = 1,
            ["artifact"] = "proof-client",
            ["propertiesSchema"] = "schemas/ui/proof.properties.json",
            ["eventsSchema"] = "schemas/ui/proof.events.json"
        }));

        var component = Assert.Single(manifest.UiComponents);
        Assert.Equal("schemas/ui/proof.properties.json", component.PropertiesSchemaPath);
        Assert.Equal("schemas/ui/proof.events.json", component.EventsSchemaPath);
    }

    /// <summary>Verifies backward compatibility with the Phase 6 single-schema declaration.</summary>
    [Fact]
    public void UiComponentKeepsLegacySingleSchemaCompatibility()
    {
        var manifest = ManifestReader.Read(CreateManifest(new JsonObject
        {
            ["id"] = "client.legacy.card",
            ["version"] = 1,
            ["artifact"] = "legacy-client",
            ["schema"] = "schemas/ui/legacy.json"
        }));

        var component = Assert.Single(manifest.UiComponents);
        Assert.Equal("schemas/ui/legacy.json", component.PropertiesSchemaPath);
        Assert.Equal("schemas/ui/legacy.json", component.EventsSchemaPath);
    }

    /// <summary>Verifies that protocol routes can invoke only capabilities declared by their module.</summary>
    [Fact]
    public void ProtocolContributionRequiresDeclaredHandler()
    {
        var document = CreateManifest(new JsonObject
        {
            ["id"] = "client.proof.card",
            ["version"] = 1,
            ["artifact"] = "proof-client",
            ["schema"] = "schemas/ui/proof.json"
        });
        document["contributes"]!["protocols"] = new JsonArray(new JsonObject
        {
            ["id"] = "protocol.example.http",
            ["version"] = 1,
            ["routeNamespace"] = "example",
            ["handler"] = "protocol.example.route",
            ["descriptor"] = "schemas/protocols/example.json",
            ["transports"] = new JsonArray("http"),
            ["authentication"] = new JsonArray("bearer"),
            ["streaming"] = "none",
            ["limits"] = new JsonObject { ["maxPayloadBytes"] = 4096, ["maxConcurrency"] = 8, ["timeout"] = "5s" },
            ["security"] = new JsonObject { ["externalContent"] = "untrusted", ["privateNetwork"] = "deny" }
        });

        var exception = Assert.Throws<InvalidDataException>(() => ManifestReader.Read(document));

        Assert.Contains("not a declared capability", exception.Message, StringComparison.Ordinal);
    }

    private static JsonObject CreateManifest(JsonObject component) => new()
    {
        ["apiVersion"] = "modules.murchalka.dev/v1",
        ["kind"] = "Module",
        ["metadata"] = new JsonObject
        {
            ["id"] = "dev.murchalka.ui-test",
            ["name"] = "UI Test",
            ["version"] = "1.0.0",
            ["publisher"] = "dev.murchalka",
            ["description"] = "Manifest parser fixture.",
            ["license"] = "Apache-2.0"
        },
        ["compatibility"] = new JsonObject { ["runtime"] = ">=0.4.0 <0.5.0", ["moduleProtocol"] = "1" },
        ["artifacts"] = new JsonObject { ["runtime"] = new JsonArray(), ["client"] = new JsonArray() },
        ["provides"] = new JsonObject { ["capabilities"] = new JsonArray() },
        ["contributes"] = new JsonObject { ["ui"] = new JsonObject { ["components"] = new JsonArray(component) } },
        ["permissions"] = new JsonObject(),
        ["health"] = new JsonObject
        {
            ["startupTimeout"] = "10s",
            ["readiness"] = new JsonObject { ["interval"] = "1s", ["timeout"] = "2s", ["failureThreshold"] = 3 },
            ["liveness"] = new JsonObject { ["interval"] = "30s", ["timeout"] = "2s", ["failureThreshold"] = 3 }
        },
        ["activation"] = new JsonObject { ["mode"] = "automaticWhenTrusted", ["failurePolicy"] = "rollback", ["hotReload"] = true, ["drainTimeout"] = "5s" }
    };
}
