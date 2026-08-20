using System.Buffers.Binary;
using System.Text.Json;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Contracts.Common;

namespace Murchalka.Runtime.ModuleGateway.Protocol;

/// <summary>Encodes and decodes length-prefixed runtime gateway frames.</summary>
public static class GatewayFrameCodec
{
    /// <summary>Writes a protocol frame to a stream.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="stream">The destination stream.</param>
    /// <param name="kind">The protocol message kind.</param>
    /// <param name="payload">The message payload.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    public static async Task WriteAsync<T>(Stream stream, string kind, T payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var frame = JsonSerializer.SerializeToUtf8Bytes(new GatewayFrame(kind, JsonSerializer.SerializeToElement(payload, ProtocolJson.Options)), ProtocolJson.Options);
        if (frame.Length > RuntimeConstants.MaximumProtocolFrameBytes) throw new InvalidDataException("Protocol frame exceeds the maximum size.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, frame.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one protocol frame from a stream.</summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The decoded gateway frame.</returns>
    public static async Task<GatewayFrame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length <= 0 || length > RuntimeConstants.MaximumProtocolFrameBytes) throw new InvalidDataException("Protocol frame length is invalid.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<GatewayFrame>(bytes, ProtocolJson.Options) ?? throw new JsonException("Protocol frame is empty.");
    }

    /// <summary>Deserializes a gateway frame payload as the requested type.</summary>
    /// <typeparam name="T">The expected payload type.</typeparam>
    /// <param name="frame">The gateway frame.</param>
    /// <returns>The deserialized payload.</returns>
    public static T PayloadAs<T>(GatewayFrame frame) => frame.Payload.Deserialize<T>(ProtocolJson.Options) ?? throw new JsonException($"Frame '{frame.Kind}' has no {typeof(T).Name} payload.");
}
