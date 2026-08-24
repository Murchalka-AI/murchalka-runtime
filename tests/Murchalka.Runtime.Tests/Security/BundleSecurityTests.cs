using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.RootSecurity.Bundles;
using Murchalka.Runtime.RootSecurity.Trust;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Security;

/// <summary>Verifies bundle signature, content integrity, and publisher trust enforcement.</summary>
public sealed class BundleSecurityTests
{
    /// <summary>Verifies a signed bundle and rejects content modified after signing.</summary>
    [Fact]
    public async Task SignedBundleIsVerifiedAndTamperingIsRejected()
    {
        using var directory = new TestDirectory();
        using var builder = new TestBundleBuilder();
        var paths = new RuntimePaths(Path.Combine(directory.Path, "runtime"));
        builder.WriteTrust(paths);
        var valid = builder.Build(Path.Combine(directory.Path, "valid"));
        var verifier = new BundleVerifier(new TrustedKeyStore(paths));

        var result = await verifier.VerifyAsync(valid.Path, TestContext.Current.CancellationToken);

        Assert.Equal(valid.Digest, result.Identity.Digest);
        Assert.Equal("dev.murchalka.hello-test", result.Manifest.Id.Value);
        var tampered = builder.Build(Path.Combine(directory.Path, "tampered"), tamperArtifactAfterSigning: true);
        var exception = await Assert.ThrowsAsync<BundleVerificationException>(() => verifier.VerifyAsync(tampered.Path, TestContext.Current.CancellationToken));
        Assert.Equal(BundleVerificationFailureKind.HashMismatch, exception.Kind);
    }

    /// <summary>Verifies that an otherwise valid bundle waits for explicit publisher trust.</summary>
    [Fact]
    public async Task UnknownPublisherWaitsForTrustInsteadOfExecuting()
    {
        using var directory = new TestDirectory();
        using var builder = new TestBundleBuilder();
        var bundle = builder.Build(Path.Combine(directory.Path, "bundle"));
        var paths = new RuntimePaths(Path.Combine(directory.Path, "runtime"));
        paths.EnsureCreated();
        var verifier = new BundleVerifier(new TrustedKeyStore(paths));

        var exception = await Assert.ThrowsAsync<BundleTrustRequiredException>(() => verifier.VerifyAsync(bundle.Path, TestContext.Current.CancellationToken));

        Assert.Equal(bundle.Digest, exception.Candidate.Identity.Digest);
        Assert.Equal("dev.murchalka.hello-test", exception.Candidate.Manifest.Id.Value);
    }
}
