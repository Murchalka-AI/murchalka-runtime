namespace Murchalka.Runtime.ModuleStore.Internal;

internal sealed record StoreMetadata(string BundleDigest, string ArchiveDigest, DateTimeOffset InstalledAt);
