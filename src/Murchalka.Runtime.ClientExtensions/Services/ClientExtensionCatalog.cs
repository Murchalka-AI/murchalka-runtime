using System.Security.Cryptography;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.ClientExtensions;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.ClientExtensions.Services;

/// <summary>Maintains an atomic content-addressed catalog for active client extensions.</summary>
public sealed class ClientExtensionCatalog : IClientExtensionCatalog
{
    private const int MaximumArtifactBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, IReadOnlyList<ClientExtensionCatalogEntry>> _entriesByModule = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClientArtifactContent> _artifacts = new(StringComparer.Ordinal);
    private TaskCompletionSource<long> _revisionChanged = NewRevisionSource();
    private long _revision;
    private DateTimeOffset _generatedAt;

    /// <summary>Creates an empty client extension catalog.</summary>
    /// <param name="timeProvider">The optional source of current time.</param>
    public ClientExtensionCatalog(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _generatedAt = _timeProvider.GetUtcNow();
    }

    /// <inheritdoc />
    public ClientExtensionCatalogSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ClientExtensionCatalogSnapshot(
                1,
                _revision,
                _generatedAt,
                _entriesByModule.Values.SelectMany(value => value).OrderBy(value => value.ExtensionId, StringComparer.Ordinal).ToArray());
        }
    }

    /// <inheritdoc />
    public void RegisterModule(InstalledBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Manifest.ClientArtifacts.Count == 0) return;
        var keyId = ReadBundleKeyId(bundle.ContentPath);
        var entries = new List<ClientExtensionCatalogEntry>();
        var contents = new Dictionary<string, ClientArtifactContent>(StringComparer.Ordinal);
        foreach (var artifact in bundle.Manifest.ClientArtifacts)
        {
            var path = ResolvePath(bundle.ContentPath, artifact.EntryPoint);
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length is 0 or > MaximumArtifactBytes) throw new InvalidDataException($"Client artifact '{artifact.Id}' has an invalid size.");
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!FixedTimeEquals(digest, artifact.Digest)) throw new InvalidDataException($"Client artifact '{artifact.Id}' digest does not match its manifest.");
            ValidateEnvelope(bytes, artifact, keyId);
            contents.Add(digest, new ClientArtifactContent(digest, bytes));
            entries.Add(new ClientExtensionCatalogEntry(
                artifact.ExtensionId,
                artifact.ExtensionVersion,
                bundle.Manifest.Id.Value,
                bundle.Manifest.Version.ToString(),
                artifact.Id,
                digest,
                bytes.LongLength,
                "/client/v1/artifacts/" + digest[7..],
                artifact.Mode,
                artifact.Targets.Select(ToWireTarget).Order(StringComparer.Ordinal).ToArray(),
                bundle.Manifest.Publisher,
                keyId,
                artifact.FallbackComponent));
        }
        var duplicate = entries.GroupBy(value => value.ExtensionId, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Client extension '{duplicate.Key}' is duplicated in module '{bundle.Manifest.Id.Value}'.");
        lock (_gate)
        {
            var conflicting = _entriesByModule.Where(pair => !string.Equals(pair.Key, bundle.Manifest.Id.Value, StringComparison.Ordinal))
                .SelectMany(pair => pair.Value).FirstOrDefault(value => entries.Any(candidate => candidate.ExtensionId == value.ExtensionId));
            if (conflicting is not null) throw new InvalidDataException($"Client extension '{conflicting.ExtensionId}' is already active.");
            RemoveModuleUnderLock(bundle.Manifest.Id.Value);
            _entriesByModule[bundle.Manifest.Id.Value] = entries;
            foreach (var pair in contents) _artifacts[pair.Key] = pair.Value;
            PublishRevisionUnderLock();
        }
    }

    /// <inheritdoc />
    public void UnregisterModule(ModuleId moduleId)
    {
        lock (_gate)
        {
            if (!_entriesByModule.ContainsKey(moduleId.Value)) return;
            RemoveModuleUnderLock(moduleId.Value);
            PublishRevisionUnderLock();
        }
    }

    /// <inheritdoc />
    public ClientArtifactContent? OpenArtifact(string digest)
    {
        ValidateDigest(digest);
        lock (_gate) return _artifacts.GetValueOrDefault(digest);
    }

    /// <inheritdoc />
    public ValueTask<long> WaitForRevisionAsync(long observedRevision, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_revision > observedRevision) return ValueTask.FromResult(_revision);
            return new ValueTask<long>(_revisionChanged.Task.WaitAsync(cancellationToken));
        }
    }

    private static void ValidateEnvelope(byte[] bytes, ClientArtifact artifact, string keyId)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Client extension envelope is invalid.");
        var extension = root.GetProperty("extension");
        var signature = root.GetProperty("signature");
        if (extension.GetProperty("apiVersion").GetString() != "client.murchalka.dev/v1" || extension.GetProperty("kind").GetString() != "ClientExtension" ||
            extension.GetProperty("id").GetString() != artifact.ExtensionId || extension.GetProperty("version").GetString() != artifact.ExtensionVersion ||
            extension.GetProperty("mode").GetString() != artifact.Mode || extension.GetProperty("fallbackComponent").GetString() != artifact.FallbackComponent ||
            signature.GetProperty("algorithm").GetString() != "ecdsa-p256-sha256" || signature.GetProperty("keyId").GetString() != keyId)
            throw new InvalidDataException($"Client extension envelope for '{artifact.Id}' does not match its verified manifest.");
    }

    private void RemoveModuleUnderLock(string moduleId)
    {
        if (!_entriesByModule.Remove(moduleId, out var prior)) return;
        foreach (var entry in prior) _artifacts.Remove(entry.ArtifactDigest);
    }

    private void PublishRevisionUnderLock()
    {
        _revision = checked(_revision + 1);
        _generatedAt = _timeProvider.GetUtcNow();
        var prior = _revisionChanged;
        _revisionChanged = NewRevisionSource();
        prior.TrySetResult(_revision);
    }

    private static TaskCompletionSource<long> NewRevisionSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ReadBundleKeyId(string contentPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(ResolvePath(contentPath, "signature/signature.json")));
        return document.RootElement.GetProperty("keyId").GetString() ?? throw new InvalidDataException("Bundle signature key id is missing.");
    }

    private static string ResolvePath(string contentPath, string relativePath)
    {
        var root = Path.GetFullPath(contentPath) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(contentPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path)) throw new InvalidDataException($"Client artifact path '{relativePath}' is invalid.");
        return path;
    }

    private static string ToWireTarget(ClientTarget target) => target switch
    {
        ClientTarget.Web => "web",
        ClientTarget.Desktop => "desktop",
        ClientTarget.Mobile => "mobile",
        ClientTarget.Xr => "xr",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown client target.")
    };

    private static bool FixedTimeEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(left), System.Text.Encoding.ASCII.GetBytes(right));

    private static void ValidateDigest(string digest)
    {
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("Client artifact digest is invalid.", nameof(digest));
    }
}
