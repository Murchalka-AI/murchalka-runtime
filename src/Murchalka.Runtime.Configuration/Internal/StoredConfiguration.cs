using System.Text.Json;

namespace Murchalka.Runtime.Configuration.Internal;

internal sealed record StoredConfiguration(string ModuleId, long Revision, string SchemaDigest, JsonElement Values, DateTimeOffset UpdatedAt);
