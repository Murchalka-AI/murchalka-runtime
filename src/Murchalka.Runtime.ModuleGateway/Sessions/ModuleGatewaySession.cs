using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.ModuleGateway.Protocol;

namespace Murchalka.Runtime.ModuleGateway.Sessions;

/// <summary>Provides serialized request-response communication with an authenticated module process.</summary>
public sealed class ModuleGatewaySession : IModuleGatewaySession
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal ModuleGatewaySession(Stream stream, ModuleHello hello, ModuleReady ready)
    {
        _stream = stream;
        Hello = hello;
        Ready = ready;
    }

    /// <summary>Gets the module hello message validated during the handshake.</summary>
    public ModuleHello Hello { get; }
    /// <summary>Gets the module readiness message validated during the handshake.</summary>
    public ModuleReady Ready { get; }
    /// <inheritdoc />
    public InstanceId InstanceId => Hello.InstanceId;

    /// <inheritdoc />
    public async Task<ModuleHealth> ProbeHealthAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var operation = CreateControl(ControlMessageKind.HealthProbe, timeout);
        var frame = await ExchangeAsync("control", operation, "health", cancellationToken).ConfigureAwait(false);
        return GatewayFrameCodec.PayloadAs<ModuleHealth>(frame);
    }

    /// <inheritdoc />
    public async Task<ControlResult> SendControlAsync(ControlMessageKind kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var operation = CreateControl(kind, timeout);
        var frame = await ExchangeAsync("control", operation, "controlResult", cancellationToken).ConfigureAwait(false);
        var result = GatewayFrameCodec.PayloadAs<ControlResult>(frame);
        if (result.OperationId != operation.OperationId) throw new InvalidDataException("Control result operation id does not match the request.");
        return result;
    }

    /// <inheritdoc />
    public async Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Deadline <= DateTimeOffset.UtcNow) throw new TimeoutException("Invocation deadline has elapsed.");
        var frame = await ExchangeAsync("invocation", invocation, "result", cancellationToken).ConfigureAwait(false);
        var result = GatewayFrameCodec.PayloadAs<ResultEnvelope>(frame);
        if (result.InvocationId != invocation.InvocationId) throw new InvalidDataException("Result invocation id does not match the request.");
        return result;
    }

    private async Task<GatewayFrame> ExchangeAsync<T>(string requestKind, T request, string responseKind, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await GatewayFrameCodec.WriteAsync(_stream, requestKind, request, cancellationToken).ConfigureAwait(false);
            var frame = await GatewayFrameCodec.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(frame.Kind, responseKind, StringComparison.Ordinal)) throw new InvalidDataException($"Expected protocol frame '{responseKind}', received '{frame.Kind}'.");
            return frame;
        }
        finally { _gate.Release(); }
    }

    private static ControlMessage CreateControl(ControlMessageKind kind, TimeSpan timeout)
    {
        using var empty = JsonDocument.Parse("{}");
        return new ControlMessage(Guid.NewGuid().ToString("N"), kind, DateTimeOffset.UtcNow.Add(timeout), empty.RootElement.Clone());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
