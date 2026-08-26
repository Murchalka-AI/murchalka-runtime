using Murchalka.Runtime.ModuleGateway.Security;

namespace Murchalka.Runtime.Tests.Security;

/// <summary>Verifies Linux PID namespace identity parsing.</summary>
public sealed class LinuxProcessIdentityVerifierTests
{
    /// <summary>Returns the innermost process identifier from Linux namespace status.</summary>
    /// <param name="status">The synthetic Linux process status.</param>
    /// <param name="expected">The expected innermost process identifier.</param>
    [Theory]
    [InlineData("Name:\tmodule\nNSpid:\t4812\t2\n", 2)]
    [InlineData("Name:\tmodule\nNSpid:\t4812\n", 4812)]
    public void ParsesInnermostNamespaceProcessId(string status, int expected)
    {
        Assert.Equal(expected, LinuxProcessIdentityVerifier.ParseInnermostNamespaceProcessId(status));
    }
}
