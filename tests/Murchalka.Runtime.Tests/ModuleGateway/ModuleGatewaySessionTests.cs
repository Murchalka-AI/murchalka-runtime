using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.ModuleGateway.Protocol;
using Murchalka.Runtime.ModuleGateway.Sessions;

namespace Murchalka.Runtime.Tests.ModuleGateway;

/// <summary>Verifies canonical protocol messages emitted by module gateway sessions.</summary>
public sealed class ModuleGatewaySessionTests
{
    /// <summary>Ensures an unresponsive module cannot hold Runtime activation indefinitely.</summary>
    [Fact]
    public async Task ControlExchangeEnforcesItsDeadlineAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            await client.ConnectAsync(endpoint.Address, endpoint.Port, TestContext.Current.CancellationToken);
            using var server = await accept;
            await using var serverStream = server.GetStream();
            var moduleId = new ModuleId("dev.murchalka.test");
            var instanceId = new InstanceId("test-instance");
            var hello = new ModuleHello(moduleId, new SemanticVersion(0, 5, 0), "sha256:" + new string('a', 64), instanceId, [1], "test", ModuleTarget.Runtime, "1", "digest", "nonce");
            var ready = new ModuleReady(moduleId, instanceId, "digest", DateTimeOffset.UtcNow);
            await using var session = new ModuleGatewaySession(serverStream, hello, ready, new DependencyEndpointsSnapshot(0, []));

            await Assert.ThrowsAsync<TimeoutException>(() =>
                session.SendControlAsync(ControlMessageKind.Activate, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Ensures mutable snapshots remain readable by strict module protocol clients.</summary>
    [Fact]
    public async Task UpdatesUseCanonicalProtocolJsonAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            await client.ConnectAsync(endpoint.Address, endpoint.Port, TestContext.Current.CancellationToken);
            using var server = await accept;
            await using var clientStream = client.GetStream();
            await using var serverStream = server.GetStream();
            var moduleId = new ModuleId("dev.murchalka.test");
            var instanceId = new InstanceId("test-instance");
            var hello = new ModuleHello(moduleId, new SemanticVersion(0, 2, 1), "sha256:" + new string('a', 64), instanceId, [1], "test", ModuleTarget.Runtime, "1", "digest", "nonce");
            var ready = new ModuleReady(moduleId, instanceId, "digest", DateTimeOffset.UtcNow);
            await using var session = new ModuleGatewaySession(serverStream, hello, ready, new DependencyEndpointsSnapshot(0, []));

            var module = RespondToUpdatesAsync(clientStream);
            await session.UpdateDependenciesAsync(new DependencyEndpointsSnapshot(7, []), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            using var values = JsonDocument.Parse("{\"model\":\"llama3.2\"}");
            await session.UpdateConfigurationAsync(new ConfigurationSnapshot(9, "sha256:config", values.RootElement.Clone()), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await module;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Ensures caller cancellation is forwarded to the module process and late results are discarded.</summary>
    [Fact]
    public async Task InvocationCancellationIsForwardedAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            await client.ConnectAsync(endpoint.Address, endpoint.Port, TestContext.Current.CancellationToken);
            using var server = await accept;
            await using var clientStream = client.GetStream();
            await using var serverStream = server.GetStream();
            var moduleId = new ModuleId("dev.murchalka.test");
            var instanceId = new InstanceId("test-instance");
            var hello = new ModuleHello(moduleId, new SemanticVersion(0, 5, 0), "sha256:" + new string('a', 64), instanceId, [1], "test", ModuleTarget.Runtime, "1", "digest", "nonce");
            var ready = new ModuleReady(moduleId, instanceId, "digest", DateTimeOffset.UtcNow);
            await using var session = new ModuleGatewaySession(serverStream, hello, ready, new DependencyEndpointsSnapshot(0, []));
            var invocation = CreateInvocation(moduleId, instanceId);
            using var cancellation = new CancellationTokenSource();
            var invocationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var module = ObserveCancellationAsync(clientStream, invocation.InvocationId, invocationObserved);

            var pending = session.InvokeAsync(invocation, cancellation.Token);
            await invocationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await module;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Ensures a module can cancel an in-flight invocation of a granted dependency.</summary>
    [Fact]
    public async Task DependencyCancellationStopsTheProviderInvocationAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            await client.ConnectAsync(endpoint.Address, endpoint.Port, TestContext.Current.CancellationToken);
            using var server = await accept;
            await using var clientStream = client.GetStream();
            await using var serverStream = server.GetStream();
            var moduleId = new ModuleId("dev.murchalka.consumer");
            var providerId = new ModuleId("dev.murchalka.provider");
            var consumerInstance = new InstanceId("consumer-instance");
            var providerInstance = new InstanceId("provider-instance");
            var capability = new CapabilityId("conformance.echo");
            var dependency = new DependencyEndpoint("echo", providerId, new SemanticVersion(0, 5, 0), capability,
                new SemanticVersion(1, 0, 0), providerInstance, new Uri("module://provider/conformance.echo"), "grant:echo");
            var hello = new ModuleHello(moduleId, new SemanticVersion(0, 5, 0), "sha256:" + new string('a', 64), consumerInstance, [1], "test", ModuleTarget.Runtime, "1", "digest", "nonce");
            var ready = new ModuleReady(moduleId, consumerInstance, "digest", DateTimeOffset.UtcNow);
            await using var session = new ModuleGatewaySession(serverStream, hello, ready, new DependencyEndpointsSnapshot(1, [dependency]));
            var providerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.SetDependencyInvoker(async (_, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { providerCancelled.TrySetResult(); throw; }
                throw new InvalidOperationException("Unreachable provider continuation.");
            });
            var invocation = CreateInvocation(moduleId, providerInstance, capability, "grant:echo");

            await GatewayFrameCodec.WriteAsync(clientStream, "capabilityInvocation", invocation, TestContext.Current.CancellationToken);
            await GatewayFrameCodec.WriteAsync(clientStream, "capabilityCancellation",
                new { invocationId = invocation.InvocationId, reason = "caller-cancelled" }, TestContext.Current.CancellationToken);

            await providerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var resultFrame = await GatewayFrameCodec.ReadAsync(clientStream, TestContext.Current.CancellationToken);
            var result = GatewayFrameCodec.PayloadAs<ResultEnvelope>(resultFrame);
            Assert.Equal(InvocationStatus.Cancelled, result.Status);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static InvocationEnvelope CreateInvocation(
        ModuleId consumer,
        InstanceId provider,
        CapabilityId? capability = null,
        string authorizationReference = "grant:test")
    {
        using var payload = JsonDocument.Parse("{}");
        return new InvocationEnvelope(Guid.NewGuid(), capability ?? new CapabilityId("test.invoke"), new SemanticVersion(1, 0, 0),
            provider, consumer, null, new InvocationScope(null, null, null, null, null, null), "test", authorizationReference,
            "correlation", "trace", null, DateTimeOffset.UtcNow.AddSeconds(30), null, "test.request@1", payload.RootElement.Clone(), null);
    }

    private static async Task ObserveCancellationAsync(Stream stream, Guid invocationId, TaskCompletionSource invocationObserved)
    {
        var invocationFrame = await GatewayFrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal("invocation", invocationFrame.Kind);
        invocationObserved.TrySetResult();
        var cancellationFrame = await GatewayFrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal("invocationCancellation", cancellationFrame.Kind);
        var cancellation = GatewayFrameCodec.PayloadAs<JsonElement>(cancellationFrame);
        Assert.Equal(invocationId, cancellation.GetProperty("invocationId").GetGuid());
        Assert.Equal("caller-cancelled", cancellation.GetProperty("reason").GetString());
        await GatewayFrameCodec.WriteAsync(stream, "result", new ResultEnvelope(invocationId, InvocationStatus.Cancelled, null,
            new ProtocolError("invocation-cancelled", ErrorCategory.Cancelled, false, "Cancelled.", null), null, [], [], null),
            TestContext.Current.CancellationToken);
    }

    private static async Task RespondToUpdatesAsync(Stream stream)
    {
        var bindingsFrame = await GatewayFrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        var bindingsControl = GatewayFrameCodec.PayloadAs<ControlMessage>(bindingsFrame);
        Assert.Equal(ControlMessageKind.UpdateBindings, bindingsControl.Kind);
        Assert.Equal(7, bindingsControl.Payload.GetProperty("bindingRevision").GetInt64());
        Assert.DoesNotContain("BindingRevision", bindingsControl.Payload.EnumerateObject().Select(property => property.Name));
        _ = bindingsControl.Payload.Deserialize<DependencyEndpointsSnapshot>(ProtocolJson.Options)
            ?? throw new InvalidDataException("Dependency snapshot was empty.");
        await GatewayFrameCodec.WriteAsync(stream, "controlResult", new ControlResult(bindingsControl.OperationId, true, null, null, null), TestContext.Current.CancellationToken);

        var configurationFrame = await GatewayFrameCodec.ReadAsync(stream, TestContext.Current.CancellationToken);
        var configurationControl = GatewayFrameCodec.PayloadAs<ControlMessage>(configurationFrame);
        Assert.Equal(ControlMessageKind.ReloadConfiguration, configurationControl.Kind);
        Assert.Equal(9, configurationControl.Payload.GetProperty("revision").GetInt64());
        Assert.DoesNotContain("Revision", configurationControl.Payload.EnumerateObject().Select(property => property.Name));
        _ = configurationControl.Payload.Deserialize<ConfigurationSnapshot>(ProtocolJson.Options)
            ?? throw new InvalidDataException("Configuration snapshot was empty.");
        await GatewayFrameCodec.WriteAsync(stream, "controlResult", new ControlResult(configurationControl.OperationId, true, null, null, null), TestContext.Current.CancellationToken);
    }
}
