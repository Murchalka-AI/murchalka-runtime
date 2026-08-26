using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Audit.Models;
using Murchalka.Runtime.Audit.Services;
using Murchalka.Runtime.Bootstrap.Composition;
using Murchalka.Runtime.Bootstrap.Hosting;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Lifecycle;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.EndToEnd;

/// <summary>Verifies complete Runtime workflows with a real module process.</summary>
public sealed class RuntimeEndToEndTests
{
    /// <summary>Verifies that an active module is safely reconciled after a runtime restart.</summary>
    [Fact]
    public async Task ActiveModuleIsReconciledAfterRuntimeRestart()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory();
        using var bundleBuilder = new TestBundleBuilder();
        var paths = new RuntimePaths(Path.Combine(directory.Path, "runtime"));
        bundleBuilder.WriteTrust(paths);
        var bundle = bundleBuilder.Build(Path.Combine(directory.Path, "bundle"));
        await using (var first = RuntimeBootstrap.Create(paths.Root, discoveryPollInterval: TimeSpan.FromMilliseconds(20)))
        {
            await first.Kernel.StartAsync(TestContext.Current.CancellationToken);
            File.Copy(bundle.Path, Path.Combine(paths.Inbox, "hello.murchalka"));
            await WaitForStateAsync(first, ModuleLifecycleState.Active);
        }

        await using var second = RuntimeBootstrap.Create(paths.Root, discoveryPollInterval: TimeSpan.FromMilliseconds(20));
        await second.Kernel.StartAsync(TestContext.Current.CancellationToken);
        var recovered = await WaitForStateAsync(second, ModuleLifecycleState.Active);
        Assert.NotNull(recovered.InstanceId);
        Assert.Single(second.Kernel.Capabilities.Snapshot());
        await second.Kernel.DisableAsync(
            new ModuleId("dev.murchalka.hello-test"),
            TestContext.Current.CancellationToken);
    }

    /// <summary>Verifies bundle discovery, activation, invocation, and graceful disablement.</summary>
    [Fact]
    public async Task DropInvokeAndDisableHelloModuleWithoutRuntimeRestart()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory();
        using var bundleBuilder = new TestBundleBuilder();
        var runtimeRoot = Path.Combine(directory.Path, "runtime");
        var paths = new RuntimePaths(runtimeRoot);
        bundleBuilder.WriteTrust(paths);
        var bundle = bundleBuilder.Build(Path.Combine(directory.Path, "bundle"));
        await using var runtime = RuntimeBootstrap.Create(runtimeRoot, discoveryPollInterval: TimeSpan.FromMilliseconds(20));
        await runtime.Kernel.StartAsync(TestContext.Current.CancellationToken);

        var partial = Path.Combine(paths.Inbox, "hello.murchalka.partial");
        File.Copy(bundle.Path, partial);
        File.Move(partial, Path.Combine(paths.Inbox, "hello.murchalka"));
        var active = await WaitForStateAsync(runtime, ModuleLifecycleState.Active);
        var provider = Assert.Single(runtime.Kernel.Capabilities.Snapshot());
        Assert.Equal(active.InstanceId, provider.InstanceId.Value);
        var generatedLock = Path.Combine(paths.Locks, "dev.murchalka.hello-test.lock.json");
        Assert.True(File.Exists(generatedLock));
        using (var lockDocument = JsonDocument.Parse(
            await File.ReadAllTextAsync(generatedLock, TestContext.Current.CancellationToken)))
        {
            Assert.Equal(active.BundleDigest, lockDocument.RootElement.GetProperty("module").GetProperty("bundleDigest").GetString());
            Assert.Empty(lockDocument.RootElement.GetProperty("dependencies").EnumerateArray());
        }

        var payload = JsonSerializer.SerializeToElement(new { name = "Murchalka" });
        var invocation = new InvocationEnvelope(Guid.NewGuid(), new CapabilityId("hello.greet"), SemanticVersion.Parse("1.0.0"), provider.InstanceId,
            new ModuleId("dev.murchalka.test-consumer"), null, new InvocationScope(null, null, null, null, null, null), "greeting", "root-test",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), null, DateTimeOffset.UtcNow.AddSeconds(5), null,
            "schemas/capabilities/hello.greet.request.schema.json", payload, null);
        var result = await runtime.Kernel.Capabilities.InvokeAsync(invocation, TestContext.Current.CancellationToken);

        Assert.Equal(InvocationStatus.Succeeded, result.Status);
        Assert.Equal("Hello, Murchalka!", result.Payload!.Value.GetProperty("greeting").GetString());
        var administrativeResult = await runtime.Kernel.InvokeAdministrativeCapabilityAsync(
            new CapabilityId("hello.greet"),
            JsonSerializer.SerializeToElement(new { name = "Administrator" }),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(InvocationStatus.Succeeded, administrativeResult.Status);
        Assert.Equal("Hello, Administrator!", administrativeResult.Payload!.Value.GetProperty("greeting").GetString());
        await WaitForAuditEventAsync(paths, "event.published");
        var invalidInvocation = invocation with { InvocationId = Guid.NewGuid(), Payload = JsonSerializer.SerializeToElement(new { unsupported = true }) };
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.Kernel.Capabilities.InvokeAsync(invalidInvocation, TestContext.Current.CancellationToken));
        var disabled = await runtime.Kernel.DisableAsync(
            new ModuleId("dev.murchalka.hello-test"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(disabled);
        Assert.Equal(ModuleLifecycleState.Disabled, disabled.State);
        Assert.Empty(runtime.Kernel.Capabilities.Snapshot());
        var reenabled = await runtime.Kernel.EnableAsync(
            new ModuleId("dev.murchalka.hello-test"),
            TestContext.Current.CancellationToken);
        Assert.NotNull(reenabled);
        Assert.Equal(ModuleLifecycleState.Active, reenabled.State);
        Assert.Single(runtime.Kernel.Capabilities.Snapshot());
        await runtime.Kernel.DisableAsync(
            new ModuleId("dev.murchalka.hello-test"),
            TestContext.Current.CancellationToken);
        Assert.Empty(HashChainedRootAudit.Verify(Path.Combine(paths.Audit, "root-audit.jsonl")));
    }

    /// <summary>Verifies that the startup inbox barrier waits for discovered bundle activation.</summary>
    [Fact]
    public async Task WaitForInboxIdleCompletesAfterDiscoveredBundlesAreProcessed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TestDirectory();
        using var bundleBuilder = new TestBundleBuilder();
        var paths = new RuntimePaths(Path.Combine(directory.Path, "runtime"));
        bundleBuilder.WriteTrust(paths);
        var bundle = bundleBuilder.Build(Path.Combine(directory.Path, "bundle"));
        File.Copy(bundle.Path, Path.Combine(paths.Inbox, "hello.murchalka"));
        await using var runtime = RuntimeBootstrap.Create(paths.Root, discoveryPollInterval: TimeSpan.FromMilliseconds(20));

        await runtime.Kernel.StartAsync(TestContext.Current.CancellationToken);
        await runtime.Kernel.WaitForInboxIdleAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var status = Assert.Single(await runtime.Kernel.GetStatusAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ModuleLifecycleState.Active, status.State);
        Assert.Single(runtime.Kernel.Capabilities.Snapshot());
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
        await runtime.Kernel.StartAsync(TestContext.Current.CancellationToken);
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

    private static async Task WaitForAuditEventAsync(RuntimePaths paths, string eventType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var path = Path.Combine(paths.Audit, "root-audit.jsonl");
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (File.Exists(path) && File.ReadLines(path).Select(line => JsonSerializer.Deserialize<RootAuditRecord>(line)).Any(value => value?.EventType == eventType)) return;
            await Task.Delay(25, timeout.Token);
        }
    }
}
