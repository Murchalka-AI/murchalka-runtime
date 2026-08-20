namespace Murchalka.Runtime.Contracts.Lifecycle;

/// <summary>Represents a module activation failure with a stable machine-readable reason.</summary>
public sealed class ModuleActivationException : InvalidOperationException
{
    /// <summary>Initializes an activation failure with an unspecified reason.</summary>
    public ModuleActivationException()
        : this("unknown", "Module activation failed.")
    {
    }

    /// <summary>Initializes an activation failure with an unspecified reason.</summary>
    /// <param name="message">The diagnostic failure message.</param>
    public ModuleActivationException(string message)
        : this("unknown", message)
    {
    }

    /// <summary>Initializes an activation failure with an unspecified reason.</summary>
    /// <param name="message">The diagnostic failure message.</param>
    /// <param name="innerException">The exception that caused the activation failure.</param>
    public ModuleActivationException(string message, Exception innerException)
        : this("unknown", message, innerException)
    {
    }

    /// <summary>Initializes a module activation failure.</summary>
    /// <param name="reasonCode">The stable machine-readable reason code.</param>
    /// <param name="message">The diagnostic failure message.</param>
    public ModuleActivationException(string reasonCode, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ReasonCode = reasonCode;
    }

    /// <summary>Initializes a module activation failure caused by another exception.</summary>
    /// <param name="reasonCode">The stable machine-readable reason code.</param>
    /// <param name="message">The diagnostic failure message.</param>
    /// <param name="innerException">The exception that caused the activation failure.</param>
    public ModuleActivationException(string reasonCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(innerException);
        ReasonCode = reasonCode;
    }

    /// <summary>Gets the stable machine-readable activation failure reason.</summary>
    public string ReasonCode { get; }
}
