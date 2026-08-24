using System.Text.Json.Nodes;
using Json.Schema;

namespace Murchalka.Runtime.Configuration.Internal;

internal sealed record ConfigurationContract(JsonSchema Schema, string SchemaDigest, JsonObject Defaults);
