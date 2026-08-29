using System.Security.Cryptography;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.ClientExtensions.Services;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.ClientExtensions;

/// <summary>Verifies atomic client extension catalog publication and integrity enforcement.</summary>
public sealed class ClientExtensionCatalogTests
{
    /// <summary>Verifies publication, revision notification, and immediate disable propagation.</summary>
    [Fact]
    public async Task RegistrationPublishesContentAndDisableRemovesItImmediately()
    {
        using var directory = new TestDirectory();
        var bundle = CreateBundle(directory.Path, "client.diagnostics");
        var catalog = new ClientExtensionCatalog();

        catalog.RegisterModule(bundle);
        var active = catalog.Snapshot();
        Assert.Equal(1, active.Revision);
        var entry = Assert.Single(active.Entries);
        Assert.NotNull(catalog.OpenArtifact(entry.ArtifactDigest));

        var revision = catalog.WaitForRevisionAsync(active.Revision, TestContext.Current.CancellationToken);
        catalog.UnregisterModule(bundle.Manifest.Id);
        Assert.Equal(2, await revision);
        Assert.Empty(catalog.Snapshot().Entries);
        Assert.Null(catalog.OpenArtifact(entry.ArtifactDigest));
    }

    /// <summary>Verifies corrupt content cannot change the active revision.</summary>
    [Fact]
    public void CorruptArtifactIsRejectedWithoutChangingTheCatalog()
    {
        using var directory = new TestDirectory();
        var bundle = CreateBundle(directory.Path, "client.diagnostics");
        var catalog = new ClientExtensionCatalog();
        File.AppendAllText(Path.Combine(bundle.ContentPath, "client", "diagnostics.json"), "corrupt");

        Assert.Throws<InvalidDataException>(() => catalog.RegisterModule(bundle));
        Assert.Equal(0, catalog.Snapshot().Revision);
        Assert.Empty(catalog.Snapshot().Entries);
    }

    private static InstalledBundle CreateBundle(string root, string extensionId)
    {
        var content = Path.Combine(root, "content");
        Directory.CreateDirectory(Path.Combine(content, "client"));
        Directory.CreateDirectory(Path.Combine(content, "signature"));
        File.WriteAllText(Path.Combine(content, "signature", "signature.json"), JsonSerializer.Serialize(new { keyId = "test-key" }));
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            extension = new
            {
                apiVersion = "client.murchalka.dev/v1",
                kind = "ClientExtension",
                id = extensionId,
                version = "0.4.0",
                mode = "declarative",
                fallbackComponent = "standard.document"
            },
            signature = new { algorithm = "ecdsa-p256-sha256", keyId = "test-key", value = Convert.ToBase64String(new byte[64]) }
        });
        var artifactPath = Path.Combine(content, "client", "diagnostics.json");
        File.WriteAllBytes(artifactPath, envelope);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(envelope));
        var manifest = new ModuleManifest(
            new ModuleId("dev.murchalka.client-diagnostics"), "Client Diagnostics", SemanticVersion.Parse("0.4.0"), "dev.murchalka", ">=0.4.0 <0.5.0", 1,
            [], [], [], [], [], [], [], [], [], [], null, [], JsonSerializer.SerializeToElement(new { }),
            new HealthPolicy(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 1),
            new ActivationPolicy("automaticWhenTrusted", "rollback", true, TimeSpan.FromSeconds(5)), null,
            JsonSerializer.SerializeToElement(new { }))
        {
            ClientArtifacts =
            [
                new ClientArtifact("client-diagnostics", extensionId, "0.4.0", new HashSet<ClientTarget> { ClientTarget.Web, ClientTarget.Desktop }, "declarative", "client/diagnostics.json", digest, "standard.document")
            ]
        };
        return new InstalledBundle("sha256:" + new string('a', 64), Path.Combine(root, "bundle.murchalka"), content, manifest, DateTimeOffset.UtcNow);
    }
}
