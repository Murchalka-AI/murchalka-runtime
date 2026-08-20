using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Lifecycle;

namespace Murchalka.Runtime.ModuleStore.Internal;

internal sealed record StoredRecord(string ModuleId, string Version, string BundleDigest, string Publisher, ModuleLifecycleState State, long Revision, DateTimeOffset UpdatedAt, string? ReasonCode, string? InstanceId, bool DesiredEnabled)
{
    /// <summary>Creates a serializable state record from the runtime domain record.</summary>
    /// <param name="record">The runtime module record.</param>
    /// <returns>The serializable state record.</returns>
    public static StoredRecord From(InstalledModuleRecord record) => new(record.ModuleId.Value, record.Version.ToString(), record.BundleDigest, record.Publisher, record.State, record.Revision, record.UpdatedAt, record.ReasonCode, record.InstanceId, record.DesiredEnabled);

    /// <summary>Converts the serialized state into the runtime domain record.</summary>
    /// <returns>The runtime module record.</returns>
    public InstalledModuleRecord ToRecord() => new(new ModuleId(ModuleId), SemanticVersion.Parse(Version), BundleDigest, Publisher, State, Revision, UpdatedAt, ReasonCode, InstanceId, DesiredEnabled);
}
