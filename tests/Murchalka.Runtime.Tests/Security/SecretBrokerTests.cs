using System.Text;
using System.Text.Json;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.Contracts.Secrets;
using Murchalka.Runtime.Secrets.Services;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Security;

/// <summary>Verifies encrypted secret persistence and Root broker authorization.</summary>
public sealed class SecretBrokerTests
{
    private static readonly string[] SecretNames = ["model/api-key"];

    /// <summary>Verifies encryption at rest and decryption after store recreation.</summary>
    [Fact]
    public async Task SecretIsEncryptedAtRestAndSurvivesStoreRestart()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        var plaintext = Encoding.UTF8.GetBytes("not-visible-on-disk");
        using (var first = new EncryptedFileSecretStore(paths))
            await first.PutAsync("model/api-key", plaintext, 0, CancellationToken.None);

        Assert.DoesNotContain("not-visible-on-disk", string.Join('\n', Directory.EnumerateFiles(paths.Secrets).Select(File.ReadAllText)), StringComparison.Ordinal);
        using var second = new EncryptedFileSecretStore(paths);
        var restored = await second.GetAsync("model/api-key", CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(plaintext, restored!.Value);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(restored.Value);
    }

    /// <summary>Verifies manifest and effective grant intersection for leases.</summary>
    [Fact]
    public async Task BrokerRequiresBothManifestRequestAndEffectiveGrant()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        using var store = new EncryptedFileSecretStore(paths);
        await store.PutAsync("model/api-key", Encoding.UTF8.GetBytes("secret"), 0, CancellationToken.None);
        var broker = new RootSecretBroker(store, new NoopRootAudit());
        var permissions = JsonSerializer.SerializeToElement(new { secrets = SecretNames });
        var manifest = Phase3TestModuleFactory.Create("dev.murchalka.secret-consumer", permissions: permissions);
        var bundle = new InstalledBundle("sha256:" + new string('b', 64), string.Empty, directory.Path, manifest, DateTimeOffset.UtcNow);
        var request = new SecretLeaseRequest("operation-1", "model/api-key", "model-inference", DateTimeOffset.UtcNow.AddMinutes(1));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.LeaseAsync(bundle,
            new PermissionDecision(true, "test", "grant", 1, JsonSerializer.SerializeToElement(new { secrets = Array.Empty<string>() }), null), request, CancellationToken.None));
        var lease = await broker.LeaseAsync(bundle,
            new PermissionDecision(true, "test", "grant", 1, permissions, null), request, CancellationToken.None);

        Assert.Equal("secret", Encoding.UTF8.GetString(Convert.FromBase64String(lease.Value)));
        Assert.True(lease.ExpiresAt <= request.Deadline);
    }
}
