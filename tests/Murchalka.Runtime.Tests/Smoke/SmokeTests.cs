namespace Murchalka.Runtime.Tests.Smoke;

/// <summary>Contains basic runtime version smoke tests.</summary>
public sealed class SmokeTests
{
    /// <summary>Verifies that the runtime exposes the coordinated Phase 7 patch version.</summary>
    [Fact]
    public void RuntimeVersionIsPhaseSevenRelease() => Assert.Equal("0.4.1", Murchalka.Runtime.Contracts.Common.RuntimeConstants.Version.ToString());
}
