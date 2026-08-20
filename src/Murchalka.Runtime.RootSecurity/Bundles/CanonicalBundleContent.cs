using System.Security.Cryptography;
using System.Text;

namespace Murchalka.Runtime.RootSecurity.Bundles;

/// <summary>Creates deterministic bundle signing content and SHA-256 digests.</summary>
public static class CanonicalBundleContent
{
    /// <summary>Gets the canonical SHA-256 digest containing only zeroes.</summary>
    public const string ZeroDigest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Creates canonical signing bytes from bundle file hashes.</summary>
    /// <param name="fileHashes">The normalized archive paths and their SHA-256 digests.</param>
    /// <returns>The canonical UTF-8 signing content.</returns>
    public static byte[] Create(IReadOnlyDictionary<string, string> fileHashes)
    {
        var builder = new StringBuilder("murchalka-bundle-v1\n");
        foreach (var pair in fileHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            builder.Append(pair.Key).Append('\n').Append(pair.Value).Append('\n');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Computes the logical bundle digest from its file hashes.</summary>
    /// <param name="fileHashes">The normalized archive paths and their SHA-256 digests.</param>
    /// <returns>The prefixed lowercase SHA-256 digest.</returns>
    public static string Digest(IReadOnlyDictionary<string, string> fileHashes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Create(fileHashes)));

    /// <summary>Computes a prefixed lowercase SHA-256 digest.</summary>
    /// <param name="value">The bytes to hash.</param>
    /// <returns>The prefixed lowercase SHA-256 digest.</returns>
    public static string Sha256(ReadOnlySpan<byte> value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(value));
}
