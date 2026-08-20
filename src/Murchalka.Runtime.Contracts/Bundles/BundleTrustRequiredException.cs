namespace Murchalka.Runtime.Contracts.Bundles;

/// <summary>Indicates that valid bundle metadata requires an explicit publisher trust decision.</summary>
public sealed class BundleTrustRequiredException : BundleVerificationException
{
    /// <summary>Initializes the exception for an untrusted bundle candidate.</summary>
    /// <param name="candidate">The structurally valid but not yet trusted bundle.</param>
    public BundleTrustRequiredException(VerifiedBundle candidate)
        : base(BundleVerificationFailureKind.PublisherUntrusted, "publisher-untrusted", $"Publisher '{candidate.Identity.Publisher}' key '{candidate.Identity.KeyId}' is not trusted.") => Candidate = candidate;

    /// <summary>Gets the untrusted bundle candidate.</summary>
    public VerifiedBundle Candidate { get; }
}
