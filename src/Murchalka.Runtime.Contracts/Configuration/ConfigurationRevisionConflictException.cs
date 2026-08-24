namespace Murchalka.Runtime.Contracts.Configuration;

/// <summary>Indicates that a configuration write used a stale expected revision.</summary>
public sealed class ConfigurationRevisionConflictException : InvalidOperationException
{
    /// <summary>Creates a revision conflict.</summary>
    /// <param name="expectedRevision">The caller's expected revision.</param>
    /// <param name="actualRevision">The current stored revision.</param>
    public ConfigurationRevisionConflictException(long expectedRevision, long actualRevision)
        : base($"Expected configuration revision {expectedRevision}, but current revision is {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    /// <summary>Gets the caller's expected revision.</summary>
    public long ExpectedRevision { get; }

    /// <summary>Gets the current stored revision.</summary>
    public long ActualRevision { get; }
}
