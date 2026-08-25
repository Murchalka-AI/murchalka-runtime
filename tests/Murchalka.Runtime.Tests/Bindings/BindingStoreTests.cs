using System.Text.Json;
using Murchalka.Runtime.Bindings.Services;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Bindings;

/// <summary>Verifies schema validation, optimistic concurrency, and durable binding revisions.</summary>
public sealed class BindingStoreTests
{
    /// <summary>Verifies that only the next revision is committed and stale administrators fail.</summary>
    [Fact]
    public async Task ReplacementIsAtomicRevisionedAndRoundTrips()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        using var store = new FileBindingStore(paths, "local");
        var document = JsonSerializer.SerializeToElement(new
        {
            apiVersion = "bindings.murchalka.dev/v1",
            kind = "ModuleBindings",
            metadata = new { installation = "local", revision = 1 },
            bindings = new[]
            {
                new
                {
                    id = "storage-choice",
                    consumer = new { module = "dev.murchalka.consumer", requirement = "records" },
                    scope = new { type = "global" },
                    provider = new { module = "dev.murchalka.storage-postgresql", capability = "storage.records", instance = "default" }
                }
            },
            policies = new { ambiguity = "fail", missingScopedBinding = "inheritParent", providerUnavailable = "fail" }
        });

        var stored = await store.ReplaceAsync(document, 0, TestContext.Current.CancellationToken);

        Assert.Equal(1, stored.Revision);
        Assert.Equal("dev.murchalka.storage-postgresql", Assert.Single(stored.Bindings).Provider.Primary.ModuleId.Value);
        Assert.True(File.Exists(paths.Bindings));
        var stale = await Assert.ThrowsAsync<BindingRevisionConflictException>(() => store.ReplaceAsync(document, 0, TestContext.Current.CancellationToken));
        Assert.Equal(1, stale.ActualRevision);
        var reloaded = await store.GetAsync(TestContext.Current.CancellationToken);
        Assert.True(JsonElement.DeepEquals(BindingDocumentJson.Serialize(stored), BindingDocumentJson.Serialize(reloaded)));
    }

    /// <summary>Verifies that a deployment-specific installation identifier is enforced.</summary>
    [Fact]
    public async Task DeploymentInstallationIdentifierIsAccepted()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        using var store = new FileBindingStore(paths, "minimal-core");
        var document = JsonSerializer.SerializeToElement(new
        {
            apiVersion = "bindings.murchalka.dev/v1",
            kind = "ModuleBindings",
            metadata = new { installation = "minimal-core", revision = 1 },
            bindings = Array.Empty<object>(),
            policies = new { ambiguity = "fail", missingScopedBinding = "inheritParent", providerUnavailable = "fail" }
        });

        var stored = await store.ReplaceAsync(document, 0, TestContext.Current.CancellationToken);

        Assert.Equal("minimal-core", stored.Installation);
    }
}
