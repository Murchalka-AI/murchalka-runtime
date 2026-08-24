namespace Murchalka.Runtime.Contracts.State;

/// <summary>Describes one authenticated module-owned state migration.</summary>
/// <param name="Id">The stable migration identifier.</param>
/// <param name="FromVersion">The required source schema version.</param>
/// <param name="ToVersion">The resulting schema version.</param>
/// <param name="ArtifactPath">The bundle-relative migration artifact path.</param>
/// <param name="Checksum">The signed SHA-256 artifact checksum.</param>
/// <param name="Transactional">Whether the provider must apply the migration atomically.</param>
/// <param name="Reversible">Whether a verified down artifact exists.</param>
/// <param name="DownArtifactPath">The optional bundle-relative down artifact path.</param>
/// <param name="RollbackStrategy">The optional explicit rollback strategy.</param>
public sealed record ModuleMigration(
    string Id,
    string FromVersion,
    string ToVersion,
    string ArtifactPath,
    string Checksum,
    bool Transactional,
    bool Reversible,
    string? DownArtifactPath,
    string? RollbackStrategy);
