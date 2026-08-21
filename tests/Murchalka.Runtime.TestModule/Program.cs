using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.ModuleGateway.Protocol;

var socketPath = Required("MURCHALKA_SOCKET");
var moduleId = new ModuleId(Required("MURCHALKA_MODULE_ID"));
var moduleVersion = SemanticVersion.Parse(Required("MURCHALKA_MODULE_VERSION"));
var bundleDigest = Required("MURCHALKA_BUNDLE_DIGEST");
var artifactId = Required("MURCHALKA_ARTIFACT_ID");
var instanceId = new InstanceId(Required("MURCHALKA_INSTANCE_ID"));
var capabilityDigest = Required("MURCHALKA_CAPABILITIES_DIGEST");
var proofKey = Convert.FromBase64String(Required("MURCHALKA_PROOF_KEY"));
var sandboxViolation = OperatingSystem.IsMacOS() && CanReadRuntimeTrustStore();

using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
await using var stream = new NetworkStream(socket, ownsSocket: false);
var hello = new ModuleHello(moduleId, moduleVersion, bundleDigest, instanceId, [1], artifactId, ModuleTarget.Runtime,
    Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), capabilityDigest, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
await GatewayFrameCodec.WriteAsync(stream, "moduleHello", hello, CancellationToken.None);
var challengeFrame = await GatewayFrameCodec.ReadAsync(stream, CancellationToken.None);
var challenge = GatewayFrameCodec.PayloadAs<RuntimeChallenge>(challengeFrame);
if (challenge.ModuleNonce != hello.Nonce || challenge.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidDataException("Runtime challenge is invalid.");
var transcript = string.Join('\n', "murchalka-module-proof-v1", hello.ModuleId.Value, hello.ModuleVersion.ToString(), hello.BundleDigest,
    hello.InstanceId.Value, hello.ArtifactId, hello.DeclaredCapabilitiesDigest, challenge.SelectedProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), challenge.ModuleNonce, challenge.RuntimeNonce);
var proof = new ModuleProof(moduleId, instanceId, challenge.RuntimeNonce, challenge.ModuleNonce, Convert.ToBase64String(HMACSHA256.HashData(proofKey, Encoding.UTF8.GetBytes(transcript))));
await GatewayFrameCodec.WriteAsync(stream, "moduleProof", proof, CancellationToken.None);
for (var index = 0; index < 3; index++)
{
    var frame = await GatewayFrameCodec.ReadAsync(stream, CancellationToken.None);
    switch (frame.Kind)
    {
        case "configurationSnapshot": _ = GatewayFrameCodec.PayloadAs<ConfigurationSnapshot>(frame); break;
        case "permissionGrantSnapshot": _ = GatewayFrameCodec.PayloadAs<PermissionGrantSnapshot>(frame); break;
        case "dependencyEndpointsSnapshot": _ = GatewayFrameCodec.PayloadAs<DependencyEndpointsSnapshot>(frame); break;
        default: throw new InvalidDataException($"Unexpected startup frame '{frame.Kind}'.");
    }
}
await GatewayFrameCodec.WriteAsync(stream, "moduleReady", new ModuleReady(moduleId, instanceId, capabilityDigest, DateTimeOffset.UtcNow), CancellationToken.None);

var running = true;
var active = false;
while (running)
{
    var frame = await GatewayFrameCodec.ReadAsync(stream, CancellationToken.None);
    switch (frame.Kind)
    {
        case "control":
            {
                var control = GatewayFrameCodec.PayloadAs<ControlMessage>(frame);
                if (control.Kind == ControlMessageKind.HealthProbe)
                {
                    await GatewayFrameCodec.WriteAsync(stream, "health", new ModuleHealth(sandboxViolation ? ModuleHealthStatus.Unhealthy : ModuleHealthStatus.Ready, DateTimeOffset.UtcNow, sandboxViolation ? ["sandbox-filesystem-escape"] : []), CancellationToken.None);
                    break;
                }
                if (control.Deadline <= DateTimeOffset.UtcNow) throw new TimeoutException("Control deadline elapsed.");
                if (control.Kind == ControlMessageKind.Activate) active = true;
                if (control.Kind == ControlMessageKind.Drain) active = false;
                await GatewayFrameCodec.WriteAsync(stream, "controlResult", new ControlResult(control.OperationId, true, null, null, null), CancellationToken.None);
                if (control.Kind == ControlMessageKind.Stop) running = false;
                break;
            }
        case "invocation":
            {
                var invocation = GatewayFrameCodec.PayloadAs<InvocationEnvelope>(frame);
                if (!active) throw new InvalidOperationException("Invocation arrived before activation.");
                var name = invocation.Payload?.GetProperty("name").GetString() ?? "world";
                var payload = JsonSerializer.SerializeToElement(new { greeting = $"Hello, {name}!" });
                var @event = new EventEnvelope(
                    Guid.NewGuid(),
                    "greeting.completed",
                    1,
                    moduleId,
                    instanceId,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.MinValue,
                    invocation.Scope.TenantId,
                    invocation.ActorReference,
                    invocation.CorrelationId,
                    invocation.InvocationId.ToString("D"),
                    invocation.Scope.PersonId ?? "global",
                    DataClassification.Public,
                    invocation.Purpose,
                    "sha256:" + new string('0', 64),
                    payload);
                await GatewayFrameCodec.WriteAsync(stream, "eventPublication", @event, CancellationToken.None);
                var publicationFrame = await GatewayFrameCodec.ReadAsync(stream, CancellationToken.None);
                var publication = GatewayFrameCodec.PayloadAs<ControlResult>(publicationFrame);
                if (!publication.Succeeded) throw new InvalidOperationException($"Event publication failed: {publication.ErrorCode}.");
                var result = new ResultEnvelope(invocation.InvocationId, InvocationStatus.Succeeded, payload, null, null, [], [], "hello-receipt");
                await GatewayFrameCodec.WriteAsync(stream, "result", result, CancellationToken.None);
                break;
            }
        default: throw new InvalidDataException($"Unexpected active frame '{frame.Kind}'.");
    }
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Required environment variable '{name}' is missing.");

static bool CanReadRuntimeTrustStore()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && directory.Name != "modules") directory = directory.Parent;
    if (directory?.Parent is null) return true;
    var path = Path.Combine(directory.Parent.FullName, "configuration", "trusted-publishers.json");
    try { _ = File.ReadAllText(path); return true; }
    catch (UnauthorizedAccessException) { return false; }
    catch (IOException) { return true; }
}
