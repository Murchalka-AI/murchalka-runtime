using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Murchalka.Runtime.RootSecurity.Json;

internal static class JsonCanonicalizer
{
    /// <summary>Serializes a JSON node using deterministic property ordering.</summary>
    /// <param name="node">The JSON node to serialize.</param>
    /// <returns>The canonical UTF-8 JSON bytes.</returns>
    public static byte[] Serialize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false })) Write(writer, node);
        return stream.ToArray();
    }

    /// <summary>Serializes a JSON node into deterministic text.</summary>
    /// <param name="node">The JSON node to serialize.</param>
    /// <returns>The canonical JSON text.</returns>
    public static string Text(JsonNode node) => Encoding.UTF8.GetString(Serialize(node));

    private static void Write(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null: writer.WriteNullValue(); break;
            case JsonObject value:
                writer.WriteStartObject();
                foreach (var pair in value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonArray value:
                writer.WriteStartArray();
                foreach (var item in value) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValue value: value.WriteTo(writer); break;
            default: throw new InvalidOperationException($"Unsupported JSON node '{node.GetType().Name}'.");
        }
    }
}
