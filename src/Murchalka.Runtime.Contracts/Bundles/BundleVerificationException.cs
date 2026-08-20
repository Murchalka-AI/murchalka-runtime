namespace Murchalka.Runtime.Contracts.Bundles;

/// <summary>Represents a fail-closed bundle verification failure.</summary>
public class BundleVerificationException : Exception
{
    /// <summary>Initializes a bundle verification exception.</summary>
    /// <param name="kind">The failure classification.</param>
    /// <param name="code">The machine-readable failure code.</param>
    /// <param name="message">The diagnostic message.</param>
    public BundleVerificationException(BundleVerificationFailureKind kind, string code, string message) : base(message)
    {
        Kind = kind;
        Code = code;
    }

    /// <summary>Gets the failure classification.</summary>
    public BundleVerificationFailureKind Kind { get; }

    /// <summary>Gets the machine-readable failure code.</summary>
    public string Code { get; }
}
