using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.ModuleGateway.Protocol;
using Murchalka.Runtime.ModuleGateway.Security;
using Murchalka.Runtime.ModuleGateway.Sessions;

namespace Murchalka.Runtime.ModuleGateway.Listeners;

/// <summary>Accepts and authenticates a single module connection over a Unix-domain socket.</summary>
public sealed class ModuleGatewayListener : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly string _socketPath;

    /// <summary>Creates and starts a listener at the specified socket path.</summary>
    /// <param name="socketPath">The Unix-domain socket path.</param>
    public ModuleGatewayListener(string socketPath)
    {
        if (!Socket.OSSupportsUnixDomainSockets) throw new PlatformNotSupportedException("The process gateway requires Unix-domain sockets.");
        _socketPath = Path.GetFullPath(socketPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
        if (File.Exists(_socketPath)) File.Delete(_socketPath);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(1);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Accepts a module connection and performs the startup protocol handshake.</summary>
    /// <param name="bundle">The installed bundle being started.</param>
    /// <param name="artifact">The selected runtime artifact.</param>
    /// <param name="expectedInstance">The expected module instance identifier.</param>
    /// <param name="expectedProcessId">The operating-system process identifier of the launched sandbox.</param>
    /// <param name="proofKey">The ephemeral key used to authenticate the launched process.</param>
    /// <param name="grant">The permission grant supplied to the module.</param>
    /// <param name="configuration">The validated configuration supplied to the module.</param>
    /// <param name="dependencies">The resolved dependency endpoints supplied to the module.</param>
    /// <param name="cancellationToken">A token that cancels the accept operation.</param>
    /// <returns>An authenticated module gateway session.</returns>
    public async Task<ModuleGatewaySession> AcceptAsync(InstalledBundle bundle, RuntimeArtifact artifact, InstanceId expectedInstance, int expectedProcessId, ReadOnlyMemory<byte> proofKey, PermissionDecision grant, ConfigurationSnapshot configuration, DependencyEndpointsSnapshot dependencies, CancellationToken cancellationToken)
    {
        var socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            var helloFrame = await GatewayFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            RequireKind(helloFrame, "moduleHello");
            var hello = GatewayFrameCodec.PayloadAs<ModuleHello>(helloFrame);
            var capabilityDigest = CapabilityDeclarations.ComputeDigest(bundle.Manifest, bundle.ContentPath);
            var processIdentityMatches = OperatingSystem.IsLinux()
                ? LinuxProcessIdentityVerifier.Matches(socket, expectedProcessId, hello.ProcessIdentity)
                : string.Equals(
                    hello.ProcessIdentity,
                    expectedProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal);
            ValidateHello(hello, bundle, artifact, expectedInstance, processIdentityMatches, capabilityDigest);
            var now = DateTimeOffset.UtcNow;
            var challenge = new RuntimeChallenge(RuntimeConstants.ProtocolVersion, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), hello.Nonce, "hmac-sha256", now, now.AddMinutes(1));
            await GatewayFrameCodec.WriteAsync(stream, "runtimeChallenge", challenge, cancellationToken).ConfigureAwait(false);
            var proofFrame = await GatewayFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            RequireKind(proofFrame, "moduleProof");
            var proof = GatewayFrameCodec.PayloadAs<ModuleProof>(proofFrame);
            ValidateProof(hello, challenge, proof, proofKey.Span);

            await GatewayFrameCodec.WriteAsync(stream, "configurationSnapshot", configuration, cancellationToken).ConfigureAwait(false);
            await GatewayFrameCodec.WriteAsync(stream, "permissionGrantSnapshot", new PermissionGrantSnapshot(grant.Revision, grant.GrantId, bundle.Digest, now, grant.ExpiresAt, grant.Grant), cancellationToken).ConfigureAwait(false);
            await GatewayFrameCodec.WriteAsync(stream, "dependencyEndpointsSnapshot", dependencies, cancellationToken).ConfigureAwait(false);
            var readyFrame = await GatewayFrameCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            RequireKind(readyFrame, "moduleReady");
            var ready = GatewayFrameCodec.PayloadAs<ModuleReady>(readyFrame);
            if (ready.ModuleId != hello.ModuleId || ready.InstanceId != hello.InstanceId || ready.EffectiveCapabilitiesDigest != capabilityDigest)
                throw new ProtocolNegotiationException("module-ready-mismatch", "ModuleReady does not match the verified module declaration.");
            return new ModuleGatewaySession(stream, hello, ready, dependencies);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static void ValidateHello(ModuleHello hello, InstalledBundle bundle, RuntimeArtifact artifact, InstanceId instance, bool processIdentityMatches, string capabilitiesDigest)
    {
        if (hello.ModuleId != bundle.Manifest.Id || hello.ModuleVersion != bundle.Manifest.Version || hello.BundleDigest != bundle.Digest || hello.InstanceId != instance ||
            !hello.ProtocolVersions.Contains(RuntimeConstants.ProtocolVersion) || hello.ArtifactId != artifact.Id || hello.Target != ModuleTarget.Runtime ||
            !processIdentityMatches || hello.DeclaredCapabilitiesDigest != capabilitiesDigest)
            throw new ProtocolNegotiationException("module-hello-mismatch", "ModuleHello does not match the verified bundle or launched process identity.");
    }

    private static void ValidateProof(ModuleHello hello, RuntimeChallenge challenge, ModuleProof proof, ReadOnlySpan<byte> key)
    {
        if (proof.ModuleId != hello.ModuleId || proof.InstanceId != hello.InstanceId || proof.RuntimeNonce != challenge.RuntimeNonce || proof.ModuleNonce != challenge.ModuleNonce)
            throw new ProtocolNegotiationException("module-proof-identity-mismatch", "Module proof identity or nonce does not match the challenge.");
        var transcript = string.Join('\n', "murchalka-module-proof-v1", hello.ModuleId.Value, hello.ModuleVersion.ToString(), hello.BundleDigest,
            hello.InstanceId.Value, hello.ArtifactId, hello.DeclaredCapabilitiesDigest, challenge.SelectedProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), challenge.ModuleNonce, challenge.RuntimeNonce);
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(transcript));
        byte[] actual;
        try { actual = Convert.FromBase64String(proof.Proof); }
        catch (FormatException) { throw new ProtocolNegotiationException("module-proof-invalid", "Module proof encoding is invalid."); }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) throw new ProtocolNegotiationException("module-proof-invalid", "Module proof verification failed.");
    }

    private static void RequireKind(GatewayFrame frame, string expected)
    {
        if (!string.Equals(frame.Kind, expected, StringComparison.Ordinal)) throw new ProtocolNegotiationException("message-order-invalid", $"Expected '{expected}', received '{frame.Kind}'.");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        if (File.Exists(_socketPath)) File.Delete(_socketPath);
        return ValueTask.CompletedTask;
    }
}
