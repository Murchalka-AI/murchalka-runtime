namespace Murchalka.Runtime.Tests.Smoke;

/// <summary>Contains basic runtime version smoke tests.</summary>
public sealed class SmokeTests
{
    /// <summary>Verifies that the runtime exposes the coordinated Phase 5 patch version.</summary>
    [Fact]
    public void RuntimeVersionIsPhaseSixRelease() => Assert.Equal("0.3.0", Murchalka.Runtime.Contracts.Common.RuntimeConstants.Version.ToString());
}
