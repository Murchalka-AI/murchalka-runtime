using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Audit.Services;
using Murchalka.Runtime.Bootstrap.Composition;
using Murchalka.Runtime.Bootstrap.Hosting;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Lifecycle;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.EndToEnd;

/// <summary>Verifies complete Phase 1 runtime workflows with a real module process.</summary>
public sealed class RuntimeEndToEndTests
{
    /// <summary>Verifies that an active module is safely reconciled after a runtime restart.</summary>
    [Fact]
    public async Task ActiveModuleIsReconciledAfterRuntimeRestart()
    {
        using var directory = new TestDirectory();
        using var bundleBuilder = new TestBundleBuilder();
        var paths = new RuntimePaths(Path.Combine(directory.Path, "runtime"));
        bundleBuilder.WriteTrust(paths);
        var bundle = bundleBuilder.Build(Path.Combine(directory.Path, "bundle"));
        await using (var first = RuntimeBootstrap.Create(paths.Root, discoveryPollInterval: TimeSpan.FromMilliseconds(20)))
        {
            await first.Kernel.StartAsync();
            File.Copy(bundle.Path, Path.Combine(paths.Inbox, "hello.murchalka"));
            await WaitForStateAsync(first, ModuleLifecycleState.Active);
        }

        await using var second = RuntimeBootstrap.Create(paths.Root, discoveryPollInterval: TimeSpan.FromMilliseconds(20));
        await second.Kernel.StartAsync();
        var recovered = await WaitForStateAsync(second, ModuleLifecycleState.Active);
        Assert.NotNull(recovered.InstanceId);
        Assert.Single(second.Kernel.Capabilities.Snapshot());
        await second.Kernel.DisableAsync(new ModuleId("dev.murchalka.hello-test"));
    }

    /// <summary>Verifies bundle discovery, activation, invocation, and graceful disablement.</summary>
    [Fact]
    public async Task DropInvokeAndDisableHelloModuleWithoutRuntimeRestart()
    {
        using var directory = new TestDirectory();
        using var bundleBuilder = new TestBundleBuilder();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var paths = new RuntimePaths(runtimeRoot);
        bundleBuilder.WriteTrust(paths);
        var bundle = bundleBuilder.Build(Path.Combine(directory.Path, "bundle"));
        await using var runtime = RuntimeBootstrap.Create(runtimeRoot, discoveryPollInterval: TimeSpan.FromMilliseconds(20));
        await runtime.Kernel.StartAsync();

        var partial = Path.Combine(paths.Inbox, "hello.murchalka.partial");
        File.Copy(bundle.Path, partial);
        File.Move(partial, Path.Combine(paths.Inbox, "hello.murchalka"));
        var active = await WaitForStateAsync(runtime, ModuleLifecycleState.Active);
        var provider = Assert.Single(runtime.Kernel.Capabilities.Snapshot());
        Assert.Equal(active.InstanceId, provider.InstanceId.Value);

        var payload = JsonSerializer.SerializeToElement(new { name = "Murchalka" });
        var invocation = new InvocationEnvelope(Guid.NewGuid(), new CapabilityId("hello.greet"), SemanticVersion.Parse("1.0.0"), provider.InstanceId,
            new ModuleId("dev.murchalka.test-consumer"), null, new InvocationScope(null, null, null, null, null, null), "greeting", "root-test",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), null, DateTimeOffset.UtcNow.AddSeconds(5), null,
            "schemas/capabilities/hello.greet.request.schema.json", payload, null);
        var result = await runtime.Kernel.Capabilities.InvokeAsync(invocation, CancellationToken.None);

        Assert.Equal(InvocationStatus.Succeeded, result.Status);
        Assert.Equal("Hello, Murchalka!", result.Payload!.Value.GetProperty("greeting").GetString());
        var invalidInvocation = invocation with { InvocationId = Guid.NewGuid(), Payload = JsonSerializer.SerializeToElement(new { unsupported = true }) };
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.Kernel.Capabilities.InvokeAsync(invalidInvocation, CancellationToken.None));
        var disabled = await runtime.Kernel.DisableAsync(new ModuleId("dev.murchalka.hello-test"));
        Assert.NotNull(disabled);
        Assert.Equal(ModuleLifecycleState.Disabled, disabled.State);
        Assert.Empty(runtime.Kernel.Capabilities.Snapshot());
        var reenabled = await runtime.Kernel.EnableAsync(new ModuleId("dev.murchalka.hello-test"));
        Assert.NotNull(reenabled);
        Assert.Equal(ModuleLifecycleState.Active, reenabled.State);
        Assert.Single(runtime.Kernel.Capabilities.Snapshot());
        await runtime.Kernel.DisableAsync(new ModuleId("dev.murchalka.hello-test"));
        Assert.Empty(HashChainedRootAudit.Verify(Path.Combine(paths.Audit, "root-audit.jsonl")));
    }

    /// <summary>Verifies default-deny outcomes for permissions and unresolved dependencies.</summary>
    /// <param name="requestsPermission">Whether the test bundle requests a privileged permission.</param>
    /// <param name="requiresDependency">Whether the test bundle declares a missing dependency.</param>
    /// <param name="expected">The expected inactive lifecycle state.</param>
    [Theory]
    [InlineData(true, false, ModuleLifecycleState.PendingPermission)]
    [InlineData(false, true, ModuleLifecycleState.PendingDependencies)]
    public async Task DefaultDenyKeepsUnresolvedModuleInactive(bool requestsPermission, bool requiresDependency, ModuleLifecycleState expected)
    {
        using var directory = new TestDirectory();
        using var bundleBuilder = new TestBundleBuilder();
        var paths = new RuntimePaths(Path.Combine(directory.Path, "runtime"));
        bundleBuilder.WriteTrust(paths);
        var bundle = bundleBuilder.Build(Path.Combine(directory.Path, "bundle"), requestProcessSpawn: requestsPermission, requireDependency: requiresDependency);
        await using var runtime = RuntimeBootstrap.Create(paths.Root, discoveryPollInterval: TimeSpan.FromMilliseconds(20));
        await runtime.Kernel.StartAsync();
        File.Copy(bundle.Path, Path.Combine(paths.Inbox, "hello.murchalka"));

        var status = await WaitForStateAsync(runtime, expected);

        Assert.Equal(expected, status.State);
        Assert.Empty(runtime.Kernel.Capabilities.Snapshot());
    }

    private static async Task<ModuleStatus> WaitForStateAsync(RuntimeApplication runtime, ModuleLifecycleState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var status = (await runtime.Kernel.GetStatusAsync(timeout.Token)).SingleOrDefault();
            if (status?.State == expected) return status;
            if (status?.State is ModuleLifecycleState.Failed or ModuleLifecycleState.Quarantined)
                throw new Xunit.Sdk.XunitException($"Module reached {status.State}: {status.ReasonCode}.");
            await Task.Delay(25, timeout.Token);
        }
    }
}
