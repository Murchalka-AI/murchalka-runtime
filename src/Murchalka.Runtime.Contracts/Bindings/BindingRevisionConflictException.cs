namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Indicates that a binding update was based on a stale revision.</summary>
public sealed class BindingRevisionConflictException : Exception
{
    /// <summary>Initializes a binding revision conflict.</summary>
    /// <param name="expectedRevision">The revision supplied by the caller.</param>
    /// <param name="actualRevision">The current durable revision.</param>
    public BindingRevisionConflictException(long expectedRevision, long actualRevision)
        : base($"Expected binding revision {expectedRevision}, but the current revision is {actualRevision}.")
    {
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    /// <summary>Gets the stale revision supplied by the caller.</summary>
    public long ExpectedRevision { get; }

    /// <summary>Gets the current durable revision.</summary>
    public long ActualRevision { get; }
}
