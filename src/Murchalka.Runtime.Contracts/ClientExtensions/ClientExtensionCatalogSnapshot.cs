namespace Murchalka.Runtime.Contracts.ClientExtensions;

/// <summary>Represents one atomic revision of the active client extension catalog.</summary>
/// <param name="SchemaVersion">The catalog schema major.</param>
/// <param name="Revision">The monotonic catalog revision.</param>
/// <param name="GeneratedAt">The revision timestamp.</param>
/// <param name="Entries">The active immutable artifacts.</param>
public sealed record ClientExtensionCatalogSnapshot(
    int SchemaVersion,
    long Revision,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ClientExtensionCatalogEntry> Entries);
