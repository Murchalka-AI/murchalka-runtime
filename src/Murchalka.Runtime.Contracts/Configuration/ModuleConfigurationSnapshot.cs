using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Configuration;

/// <summary>Contains one validated immutable module configuration revision.</summary>
/// <param name="ModuleId">The configured module identifier.</param>
/// <param name="Revision">The monotonic configuration revision.</param>
/// <param name="SchemaDigest">The digest of the schema used for validation.</param>
/// <param name="Values">The merged default and administrator-provided values.</param>
/// <param name="UpdatedAt">The trusted update timestamp.</param>
public sealed record ModuleConfigurationSnapshot(
    ModuleId ModuleId,
    long Revision,
    string SchemaDigest,
    JsonElement Values,
    DateTimeOffset UpdatedAt);
