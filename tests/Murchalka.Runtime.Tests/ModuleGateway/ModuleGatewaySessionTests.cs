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
