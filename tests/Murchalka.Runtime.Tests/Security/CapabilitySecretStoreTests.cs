using System.Text;
using Murchalka.Runtime.Secrets.Services;

namespace Murchalka.Runtime.Tests.Security;

/// <summary>Verifies Root routing through an independently loaded secrets provider.</summary>
public sealed class CapabilitySecretStoreTests
{
    /// <summary>Verifies provider selection, Root invocation identity, and secret material round-trip.</summary>
    [Fact]
    public async Task StoreRoutesThroughSingleActiveProvider()
    {
        var registry = new CapturingSecretCapabilityRegistry();
        using var store = new CapabilitySecretStore(registry);
        var value = Encoding.UTF8.GetBytes("secret");

        var version = await store.PutAsync("model/api-key", value, 0, TestContext.Current.CancellationToken);
        var material = await store.GetAsync("model/api-key", TestContext.Current.CancellationToken);

        Assert.Equal(1, version.Revision);
        Assert.NotNull(material);
        Assert.Equal(value, material.Value);
        Assert.Equal("dev.murchalka.runtime", registry.LastInvocation!.ConsumerModuleId.Value);
        Assert.Equal("root-secret-broker", registry.LastInvocation.Purpose);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(material.Value);
    }

    /// <summary>Verifies that ambiguous provider selection fails closed.</summary>
    [Fact]
    public async Task MultipleProvidersRequireExplicitBinding()
    {
        using var store = new CapabilitySecretStore(new CapturingSecretCapabilityRegistry(providerCount: 2));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.GetAsync("model/api-key", TestContext.Current.CancellationToken));

        Assert.Contains("explicit Root provider binding", exception.Message, StringComparison.Ordinal);
    }
}
