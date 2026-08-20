using System.Text.Json;

namespace Murchalka.Runtime.ModuleGateway.Protocol;

/// <summary>Represents one framed message exchanged through the module gateway.</summary>
/// <param name="Kind">The protocol message kind.</param>
/// <param name="Payload">The JSON message payload.</param>
public sealed record GatewayFrame(string Kind, JsonElement Payload);
