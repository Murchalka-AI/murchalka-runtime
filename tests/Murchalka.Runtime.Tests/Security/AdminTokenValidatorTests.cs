using Murchalka.Runtime.Host.Security;

namespace Murchalka.Runtime.Tests.Security;

/// <summary>Verifies control-plane bearer token validation.</summary>
public sealed class AdminTokenValidatorTests
{
    /// <summary>Accepts only the exact bearer token loaded from disk.</summary>
    [Fact]
    public void ExactBearerTokenIsRequired()
    {
        var path = Path.GetTempFileName();
        try
        {
            var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(path, token + Environment.NewLine);
            var validator = AdminTokenValidator.Load(path);

            Assert.True(validator.IsAuthorized("Bearer " + token));
            Assert.False(validator.IsAuthorized(token));
            Assert.False(validator.IsAuthorized("Bearer " + token + "x"));
            Assert.False(validator.IsAuthorized(null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Rejects short low-entropy credentials at startup.</summary>
    [Fact]
    public void ShortTokensAreRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not-secret");
            Assert.Throws<InvalidDataException>(() => AdminTokenValidator.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
