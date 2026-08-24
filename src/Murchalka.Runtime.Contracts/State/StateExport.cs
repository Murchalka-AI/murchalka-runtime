using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.State;

/// <summary>Describes an authenticated exported module namespace artifact.</summary>
/// <param name="ExportId">The stable export identifier.</param>
/// <param name="ModuleId">The owning module identifier.</param>
/// <param name="Namespace">The exported namespace.</param>
/// <param name="SchemaVersion">The exported schema version.</param>
/// <param name="ContentPath">The Runtime-owned export artifact path.</param>
/// <param name="ContentDigest">The SHA-256 digest of the artifact.</param>
/// <param name="CreatedAt">The trusted creation timestamp.</param>
public sealed record StateExport(
    string ExportId,
    ModuleId ModuleId,
    string Namespace,
    string SchemaVersion,
    string ContentPath,
    string ContentDigest,
    DateTimeOffset CreatedAt);
