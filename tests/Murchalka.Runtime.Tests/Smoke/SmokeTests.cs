namespace Murchalka.Runtime.Tests.Smoke;

/// <summary>Contains basic runtime version smoke tests.</summary>
public sealed class SmokeTests
{
    /// <summary>Verifies that the runtime exposes the Phase 1 version.</summary>
    [Fact]
    public void RuntimeVersionIsPhaseOne() => Assert.Equal("0.1.0", Murchalka.Runtime.Contracts.Common.RuntimeConstants.Version.ToString());
}
