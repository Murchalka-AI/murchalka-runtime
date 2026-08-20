using System.Security.Cryptography;
using System.Text;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Capabilities;

/// <summary>Computes the authenticated digest of manifest-declared capabilities.</summary>
public static class CapabilityDeclarations
{
    /// <summary>Computes a canonical SHA-256 digest for capability ids, versions, and contract schemas.</summary>
    /// <param name="manifest">The validated module manifest.</param>
    /// <param name="contentPath">The immutable bundle content path.</param>
    /// <returns>The canonical SHA-256 digest.</returns>
    public static string ComputeDigest(ModuleManifest manifest, string contentPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentPath);
        var builder = new StringBuilder("murchalka-capabilities-v1\n");
        foreach (var capability in manifest.Capabilities.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ThenBy(value => value.Version))
        {
            var path = Path.GetFullPath(Path.Combine(contentPath, capability.ContractPath.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(contentPath) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"Capability contract '{capability.ContractPath}' escapes bundle content.");
            var schemaDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            builder.Append(capability.Id.Value).Append('@').Append(capability.Version).Append(':').Append(schemaDigest).Append('\n');
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
