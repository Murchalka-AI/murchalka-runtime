using System.Text;
using System.Text.Json;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.Contracts.Secrets;
using Murchalka.Runtime.Secrets.Services;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Security;

/// <summary>Verifies Root broker authorization and bounded secret leases.</summary>
public sealed class SecretBrokerTests
{
    private static readonly string[] SecretNames = ["model/api-key"];

    /// <summary>Verifies manifest and effective grant intersection for leases.</summary>
    [Fact]
    public async Task BrokerRequiresBothManifestRequestAndEffectiveGrant()
    {
        using var directory = new TestDirectory();
        using var store = new InMemorySecretStore();
        await store.PutAsync("model/api-key", Encoding.UTF8.GetBytes("secret"), 0, TestContext.Current.CancellationToken);
        var broker = new RootSecretBroker(store, new NoopRootAudit());
        var permissions = JsonSerializer.SerializeToElement(new { secrets = SecretNames });
        var manifest = Phase3TestModuleFactory.Create("dev.murchalka.secret-consumer", permissions: permissions);
        var bundle = new InstalledBundle("sha256:" + new string('b', 64), string.Empty, directory.Path, manifest, DateTimeOffset.UtcNow);
        var request = new SecretLeaseRequest("operation-1", "model/api-key", "model-inference", DateTimeOffset.UtcNow.AddMinutes(1));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.LeaseAsync(bundle,
            new PermissionDecision(true, "test", "grant", 1, JsonSerializer.SerializeToElement(new { secrets = Array.Empty<string>() }), null), request, TestContext.Current.CancellationToken));
        var lease = await broker.LeaseAsync(bundle,
            new PermissionDecision(true, "test", "grant", 1, permissions, null), request, TestContext.Current.CancellationToken);

        Assert.Equal("secret", Encoding.UTF8.GetString(Convert.FromBase64String(lease.Value)));
        Assert.True(lease.ExpiresAt <= request.Deadline);
    }
}
