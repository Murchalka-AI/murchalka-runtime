using System.Security.Cryptography;
using System.Text;

namespace Murchalka.Runtime.Host.Security;

/// <summary>Validates bearer credentials for the loopback Runtime control plane.</summary>
public sealed class AdminTokenValidator : IDisposable
{
    private readonly byte[] token;
    private bool disposed;

    private AdminTokenValidator(byte[] token)
    {
        this.token = token;
    }

    /// <summary>Loads a high-entropy administrative token from a protected file.</summary>
    /// <param name="path">The path to the token file.</param>
    /// <returns>A validator that owns an in-memory copy of the token.</returns>
    public static AdminTokenValidator Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = File.ReadAllText(path).Trim();
        if (value.Length < 43 || value.Length > 256 || value.Any(character => character is < '!' or > '~'))
            throw new InvalidDataException("The administrative token must contain at least 256 bits of printable entropy.");
        return new AdminTokenValidator(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Checks an HTTP Authorization header using a fixed-time comparison.</summary>
    /// <param name="authorization">The complete Authorization header.</param>
    /// <returns><see langword="true"/> when the header contains the configured bearer token.</returns>
    public bool IsAuthorized(string? authorization)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var candidate = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
        try
        {
            return candidate.Length == token.Length && CryptographicOperations.FixedTimeEquals(candidate, token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    /// <summary>Clears the in-memory token copy.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CryptographicOperations.ZeroMemory(token);
    }
}
