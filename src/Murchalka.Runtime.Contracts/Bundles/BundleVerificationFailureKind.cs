namespace Murchalka.Runtime.Contracts.Bundles;

/// <summary>Classifies fail-closed bundle verification failures.</summary>
public enum BundleVerificationFailureKind
{
    /// <summary>The archive structure or required metadata is invalid.</summary>
    InvalidArchive,
    /// <summary>The module manifest is invalid.</summary>
    InvalidManifest,
    /// <summary>The module lock is invalid.</summary>
    InvalidLock,
    /// <summary>A signed file hash does not match the payload.</summary>
    HashMismatch,
    /// <summary>An artifact digest does not match its declaration.</summary>
    ArtifactMismatch,
    /// <summary>The publisher signature is invalid.</summary>
    SignatureInvalid,
    /// <summary>The publisher key is not trusted locally.</summary>
    PublisherUntrusted,
    /// <summary>The bundle is incompatible with this Runtime.</summary>
    Incompatible,
    /// <summary>The archive contains unsafe content or paths.</summary>
    UnsafeContent
}
