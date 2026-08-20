using Json.Schema;

namespace Murchalka.Runtime.Capabilities.Internal;

internal sealed record ContractPolicy(JsonSchema Request, JsonSchema Response, int MaximumPayloadBytes);
