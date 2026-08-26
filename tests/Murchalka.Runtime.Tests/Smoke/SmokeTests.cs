namespace Murchalka.Runtime.Tests.Smoke;

/// <summary>Contains basic runtime version smoke tests.</summary>
public sealed class SmokeTests
{
    /// <summary>Verifies that the runtime exposes the coordinated Phase 5 patch version.</summary>
    [Fact]
    public void RuntimeVersionIsPhaseFivePatch() => Assert.Equal("0.2.17", Murchalka.Runtime.Contracts.Common.RuntimeConstants.Version.ToString());
}
