using System.Text.Json;
using Murchalka.Runtime.Contracts.Common;

namespace Murchalka.Runtime.RootSecurity.Trust;

/// <summary>Loads trusted publisher and permission-grant authority keys from runtime configuration.</summary>
public sealed class TrustedKeyStore
{
    private readonly RuntimePaths _paths;

    /// <summary>Creates a trusted key store.</summary>
    /// <param name="paths">The runtime filesystem paths.</param>
    public TrustedKeyStore(RuntimePaths paths) => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>Finds a trusted key for a publisher.</summary>
    /// <param name="publisher">The publisher identifier.</param>
    /// <param name="keyId">The key identifier.</param>
    /// <returns>The trusted key, or <see langword="null"/> when it is not configured.</returns>
    public TrustedKey? FindPublisherKey(string publisher, string keyId)
    {
        var root = Load();
        if (!root.TryGetProperty("publishers", out var publishers) ||
            !publishers.TryGetProperty(publisher, out var publisherNode) ||
            !publisherNode.TryGetProperty("keys", out var keys) ||
            !keys.TryGetProperty(keyId, out var key)) return null;
        return Parse(keyId, key);
    }

    /// <summary>Finds a trusted permission-grant signing authority.</summary>
    /// <param name="keyId">The key identifier.</param>
    /// <returns>The trusted key, or <see langword="null"/> when it is not configured.</returns>
    public TrustedKey? FindGrantAuthority(string keyId)
    {
        var root = Load();
        if (!root.TryGetProperty("grantAuthorities", out var authorities) || !authorities.TryGetProperty(keyId, out var key)) return null;
        return Parse(keyId, key);
    }

    private JsonElement Load()
    {
        if (!File.Exists(_paths.TrustedPublishers)) return JsonDocument.Parse("{}").RootElement.Clone();
        using var stream = new FileStream(_paths.TrustedPublishers, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 16 });
        return document.RootElement.Clone();
    }

    private static TrustedKey Parse(string keyId, JsonElement element)
    {
        var algorithm = element.GetProperty("algorithm").GetString() ?? throw new InvalidDataException("Trusted key algorithm is missing.");
        var publicKeyPem = element.GetProperty("publicKeyPem").GetString() ?? throw new InvalidDataException("Trusted public key is missing.");
        if (!string.Equals(algorithm, "ecdsa-p256-sha256", StringComparison.Ordinal)) throw new InvalidDataException($"Unsupported trusted key algorithm '{algorithm}'.");
        return new TrustedKey(keyId, algorithm, publicKeyPem);
    }
}
