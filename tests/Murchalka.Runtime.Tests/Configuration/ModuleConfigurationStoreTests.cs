using System.Text.Json;
using Murchalka.Runtime.Configuration.Services;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Configuration;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Configuration;

/// <summary>Verifies schema validation, default merging, and revision policies.</summary>
public sealed class ModuleConfigurationStoreTests
{
    /// <summary>Verifies recursive defaults and fail-closed schema validation.</summary>
    [Fact]
    public async Task DefaultsAreMergedAndEveryRevisionIsSchemaValidated()
    {
        using var directory = new TestDirectory();
        var bundle = CreateBundle(directory.Path, ConfigurationRestartPolicy.Reload);
        using var store = new FileModuleConfigurationStore(new RuntimePaths(Path.Combine(directory.Path, "runtime")));

        var initial = await store.GetAsync(bundle, CancellationToken.None);
        var updated = await store.ReplaceAsync(bundle, JsonSerializer.SerializeToElement(new { nested = new { count = 7 } }), 0, CancellationToken.None);

        Assert.Equal("sqlite", initial.Values.GetProperty("provider").GetString());
        Assert.Equal(7, updated.Values.GetProperty("nested").GetProperty("count").GetInt32());
        Assert.True(updated.Values.GetProperty("nested").GetProperty("enabled").GetBoolean());
        Assert.Equal(1, updated.Revision);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceAsync(bundle, JsonSerializer.SerializeToElement(new { nested = new { count = -1 } }), 1, CancellationToken.None));
        await Assert.ThrowsAsync<ConfigurationRevisionConflictException>(() => store.ReplaceAsync(bundle, JsonSerializer.SerializeToElement(new { }), 0, CancellationToken.None));
    }

    /// <summary>Verifies that immutable configuration accepts only its initial revision.</summary>
    [Fact]
    public async Task ImmutableConfigurationRejectsSecondRevision()
    {
        using var directory = new TestDirectory();
        var bundle = CreateBundle(directory.Path, ConfigurationRestartPolicy.Immutable);
        using var store = new FileModuleConfigurationStore(new RuntimePaths(Path.Combine(directory.Path, "runtime")));
        await store.ReplaceAsync(bundle, JsonSerializer.SerializeToElement(new { }), 0, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceAsync(bundle, JsonSerializer.SerializeToElement(new { }), 1, CancellationToken.None));
    }

    private static InstalledBundle CreateBundle(string root, ConfigurationRestartPolicy policy)
    {
        var content = Path.Combine(root, "content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "configuration.schema.json"), """
        { "$schema":"https://json-schema.org/draft/2020-12/schema", "type":"object", "additionalProperties":false,
          "properties": { "provider":{"type":"string"}, "nested":{"type":"object","additionalProperties":false,"properties":{"enabled":{"type":"boolean"},"count":{"type":"integer","minimum":0}},"required":["enabled","count"]} },
          "required":["provider","nested"] }
        """);
        File.WriteAllText(Path.Combine(content, "defaults.json"), "{\"provider\":\"sqlite\",\"nested\":{\"enabled\":true,\"count\":1}}");
        var manifest = Phase3TestModuleFactory.Create("dev.murchalka.config-test") with
        {
            Configuration = new ConfigurationDeclaration("configuration.schema.json", "defaults.json", policy)
        };
        return new InstalledBundle("sha256:" + new string('a', 64), Path.Combine(root, "bundle.murchalka"), content, manifest, DateTimeOffset.UtcNow);
    }
}
