namespace Murchalka.Runtime.Contracts.Permissions;

/// <summary>Indicates that a permission grant was changed after an administrator read it.</summary>
public sealed class PermissionGrantRevisionConflictException : Exception
{
    /// <summary>Creates a permission grant revision conflict.</summary>
    /// <param name="expectedRevision">The revision supplied by the administrator.</param>
    /// <param name="actualRevision">The current stored revision.</param>
    public PermissionGrantRevisionConflictException(long expectedRevision, long actualRevision)
        : base($"Expected permission grant revision {expectedRevision}, but current revision is {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    /// <summary>Gets the revision supplied by the administrator.</summary>
    public long ExpectedRevision { get; }

    /// <summary>Gets the current stored revision.</summary>
    public long ActualRevision { get; }
}
