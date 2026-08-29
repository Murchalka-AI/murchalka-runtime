using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.RootSecurity.Internal;
using Murchalka.Runtime.RootSecurity.Manifests;
using Murchalka.Runtime.RootSecurity.Trust;

namespace Murchalka.Runtime.RootSecurity.Bundles;

/// <summary>Validates bundle structure, hashes, metadata, compatibility, trust, and signatures.</summary>
public sealed class BundleVerifier : IBundleVerifier
{
    private const string ManifestPath = "manifest/murchalka.module.yaml";
    private const string LockPath = "manifest/module.lock.json";
    private const string HashesPath = "manifest/file-hashes.json";
    private const string SignaturePath = "signature/signature.json";
    private readonly TrustedKeyStore _trust;
    private readonly CanonicalSchemaValidator _schemas = CanonicalSchemaValidator.CreateBundled();
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a bundle verifier.</summary>
    /// <param name="trust">The trusted key store.</param>
    /// <param name="timeProvider">The optional source of current time.</param>
    public BundleVerifier(TrustedKeyStore trust, TimeProvider? timeProvider = null)
    {
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<VerifiedBundle> VerifyAsync(string stagedPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        var info = new FileInfo(stagedPath);
        if (!info.Exists || info.Length == 0 || info.Length > RuntimeConstants.MaximumBundleBytes)
            throw Failure(BundleVerificationFailureKind.InvalidArchive, "bundle-size-invalid", "Bundle is missing, empty, or exceeds the size limit.");
        try
        {
            var archiveDigest = "sha256:" + await HashFileAsync(stagedPath, cancellationToken).ConfigureAwait(false);
            await using var stream = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entries = ValidateEntries(archive);
            var hashesDocument = ParseJson(await ReadEntryAsync(Required(entries, HashesPath), cancellationToken).ConfigureAwait(false), HashesPath);
            var declaredHashes = ParseHashes(hashesDocument);
            VerifyCoverage(entries, declaredHashes);
            await VerifyHashesAsync(entries, declaredHashes, cancellationToken).ConfigureAwait(false);
            var canonical = CanonicalBundleContent.Create(declaredHashes);
            var bundleDigest = CanonicalBundleContent.Digest(declaredHashes);

            var signatureDocument = ParseJson(await ReadEntryAsync(Required(entries, SignaturePath), cancellationToken).ConfigureAwait(false), SignaturePath);
            var signature = ReadSignature(signatureDocument);
            var manifestNode = await ParseStructuredEntryAsync(Required(entries, ManifestPath), ".yaml", cancellationToken).ConfigureAwait(false);
            var manifestReport = _schemas.ValidateJson("module-manifest.schema.json", manifestNode);
            if (!manifestReport.IsValid) throw Failure(BundleVerificationFailureKind.InvalidManifest, "manifest-schema-invalid", string.Join("; ", manifestReport.Violations.Select(v => $"{v.InstanceLocation}:{v.Message}")));
            var manifest = ManifestReader.Read(manifestNode);
            if (!string.Equals(manifest.Publisher, signature.Publisher, StringComparison.Ordinal)) throw Failure(BundleVerificationFailureKind.SignatureInvalid, "publisher-mismatch", "Manifest publisher does not match the signing publisher.");
            VerifyCompatibility(manifest);

            var lockNode = ParseJson(await ReadEntryAsync(Required(entries, LockPath), cancellationToken).ConfigureAwait(false), LockPath);
            var lockReport = _schemas.ValidateJson("module-lock.schema.json", lockNode);
            if (!lockReport.IsValid) throw Failure(BundleVerificationFailureKind.InvalidLock, "lock-schema-invalid", string.Join("; ", lockReport.Violations.Select(v => $"{v.InstanceLocation}:{v.Message}")));
            VerifyLock(lockNode, manifest, bundleDigest, entries);
            await VerifyContributionDocumentsAsync(manifest, entries, cancellationToken).ConfigureAwait(false);
            VerifyArtifacts(manifest, entries);
            VerifySupplyChainEntries(entries);
            var identity = new BundleIdentity(bundleDigest, signature.Publisher, signature.KeyId);
            var candidate = new VerifiedBundle(stagedPath, identity, archiveDigest, manifest, declaredHashes, _timeProvider.GetUtcNow());
            var trustedKey = _trust.FindPublisherKey(signature.Publisher, signature.KeyId);
            if (trustedKey is null) throw new BundleTrustRequiredException(candidate);
            VerifySignature(trustedKey, canonical, signature.Value);
            return candidate;
        }
        catch (BundleVerificationException) { throw; }
        catch (InvalidDataException exception) { throw Failure(BundleVerificationFailureKind.InvalidArchive, "archive-invalid", exception.Message); }
        catch (JsonException exception) { throw Failure(BundleVerificationFailureKind.InvalidArchive, "metadata-json-invalid", exception.Message); }
        catch (CryptographicException exception) { throw Failure(BundleVerificationFailureKind.SignatureInvalid, "signature-invalid", exception.Message); }
    }

    private static Dictionary<string, ZipArchiveEntry> ValidateEntries(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > RuntimeConstants.MaximumArchiveEntries) throw new InvalidDataException("Archive entry count is invalid.");
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.Length == 0 || path.StartsWith('/') || path.Contains("//", StringComparison.Ordinal) || path.Split('/').Any(segment => segment is "" or "." or ".."))
                throw Failure(BundleVerificationFailureKind.UnsafeContent, "archive-path-unsafe", $"Unsafe archive path '{entry.FullName}'.");
            if (entry.Name.Length == 0) continue;
            expanded = checked(expanded + entry.Length);
            if (expanded > RuntimeConstants.MaximumExpandedBytes) throw new InvalidDataException("Expanded archive exceeds the size limit.");
            if (!result.TryAdd(path, entry)) throw new InvalidDataException($"Duplicate archive entry '{path}'.");
        }
        Required(result, ManifestPath); Required(result, LockPath); Required(result, HashesPath); Required(result, SignaturePath);
        return result;
    }

    private static Dictionary<string, string> ParseHashes(JsonNode node)
    {
        var root = node.AsObject();
        if (root.Count != 3 || root["schemaVersion"]?.GetValue<int>() != 1 || root["algorithm"]?.GetValue<string>() != "sha256" || root["files"] is not JsonObject files)
            throw Failure(BundleVerificationFailureKind.HashMismatch, "hash-manifest-invalid", "file-hashes.json is not canonical version 1 SHA-256 metadata.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in files)
        {
            var digest = pair.Value?.GetValue<string>() ?? string.Empty;
            if (!IsDigest(digest) || !result.TryAdd(pair.Key, digest)) throw Failure(BundleVerificationFailureKind.HashMismatch, "hash-entry-invalid", $"Invalid hash entry '{pair.Key}'.");
        }
        return result;
    }

    private static void VerifyCoverage(IReadOnlyDictionary<string, ZipArchiveEntry> entries, IReadOnlyDictionary<string, string> hashes)
    {
        var expected = entries.Keys.Where(path => path != HashesPath && !path.StartsWith("signature/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(hashes.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw Failure(BundleVerificationFailureKind.HashMismatch, "hash-coverage-invalid", "Hash manifest must cover every non-signature payload entry exactly once.");
    }

    private static async Task VerifyHashesAsync(IReadOnlyDictionary<string, ZipArchiveEntry> entries, IReadOnlyDictionary<string, string> hashes, CancellationToken cancellationToken)
    {
        foreach (var pair in hashes)
        {
            var bytes = await ReadEntryAsync(Required(entries, pair.Key), cancellationToken).ConfigureAwait(false);
            if (pair.Key == LockPath)
            {
                var node = ParseJson(bytes, LockPath).AsObject();
                var module = node["module"]?.AsObject() ?? throw new InvalidDataException("Lock module metadata is missing.");
                module["bundleDigest"] = CanonicalBundleContent.ZeroDigest;
                bytes = JsonSerializer.SerializeToUtf8Bytes(node);
            }
            var actual = CanonicalBundleContent.Sha256(bytes);
            if (!FixedEquals(actual, pair.Value)) throw Failure(BundleVerificationFailureKind.HashMismatch, "file-hash-mismatch", $"Hash mismatch for '{pair.Key}'.");
        }
    }

    private static SignatureMetadata ReadSignature(JsonNode node)
    {
        var root = node.AsObject();
        if (root.Count != 5 || root["schemaVersion"]?.GetValue<int>() != 1 || root["algorithm"]?.GetValue<string>() != "ecdsa-p256-sha256")
            throw Failure(BundleVerificationFailureKind.SignatureInvalid, "signature-metadata-invalid", "Signature metadata is invalid.");
        return new SignatureMetadata(root["publisher"]!.GetValue<string>(), root["keyId"]!.GetValue<string>(), root["signature"]!.GetValue<string>());
    }

    private static void VerifySignature(TrustedKey key, byte[] canonical, string value)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(value); }
        catch (FormatException) { throw Failure(BundleVerificationFailureKind.SignatureInvalid, "signature-encoding-invalid", "Bundle signature is not Base64."); }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(key.PublicKeyPem);
        if (!ecdsa.VerifyData(canonical, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw Failure(BundleVerificationFailureKind.SignatureInvalid, "signature-verification-failed", "Bundle signature verification failed.");
    }

    private static void VerifyCompatibility(ModuleManifest manifest)
    {
        if (manifest.ProtocolVersion != RuntimeConstants.ProtocolVersion) throw Failure(BundleVerificationFailureKind.Incompatible, "protocol-incompatible", $"Protocol major {manifest.ProtocolVersion} is not supported.");
        if (!VersionRangeExpression.Parse(manifest.RuntimeCompatibility).Satisfies(RuntimeConstants.Version))
            throw Failure(BundleVerificationFailureKind.Incompatible, "runtime-incompatible", $"Runtime {RuntimeConstants.Version} does not satisfy '{manifest.RuntimeCompatibility}'.");
        var os = CurrentOs();
        var architecture = CurrentArchitecture();
        if (!manifest.RuntimeArtifacts.Any(artifact => artifact.Mode == "process" && (artifact.OperatingSystems.Count == 0 || artifact.OperatingSystems.Contains(os)) && (artifact.Architectures.Count == 0 || artifact.Architectures.Contains(architecture)) && artifact.ProtocolVersion == RuntimeConstants.ProtocolVersion))
            throw Failure(BundleVerificationFailureKind.Incompatible, "artifact-incompatible", $"No compatible process artifact for {os}-{architecture}.");
    }

    private void VerifyLock(JsonNode node, ModuleManifest manifest, string bundleDigest, IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        var root = node.AsObject();
        var module = root["module"]!.AsObject();
        if (module["id"]!.GetValue<string>() != manifest.Id.Value || module["version"]!.GetValue<string>() != manifest.Version.ToString() || module["bundleDigest"]!.GetValue<string>() != bundleDigest)
            throw Failure(BundleVerificationFailureKind.InvalidLock, "lock-identity-mismatch", "Lock identity does not match the verified manifest and bundle digest.");
        var contracts = root["contracts"]!.AsArray().Select(value => value!.AsObject()).ToArray();
        foreach (var capability in manifest.Capabilities)
        {
            var item = contracts.SingleOrDefault(value => value["id"]!.GetValue<string>() == capability.Id.Value && value["version"]!.GetValue<string>() == capability.Version.ToString())
                ?? throw Failure(BundleVerificationFailureKind.InvalidLock, "lock-contract-missing", $"Capability '{capability.Id}' is absent from lock contracts.");
            var bytes = ReadEntryAsync(Required(entries, capability.ContractPath), CancellationToken.None).GetAwaiter().GetResult();
            if (!FixedEquals(CanonicalBundleContent.Sha256(bytes), item["schemaDigest"]!.GetValue<string>())) throw Failure(BundleVerificationFailureKind.InvalidLock, "contract-digest-mismatch", $"Contract digest mismatch for '{capability.Id}'.");
            var contractNode = ParseJson(bytes, capability.ContractPath);
            var report = _schemas.ValidateJson("capability.schema.json", contractNode);
            if (!report.IsValid) throw Failure(BundleVerificationFailureKind.InvalidManifest, "capability-contract-invalid", $"Capability contract '{capability.Id}' does not satisfy the canonical schema.");
            var contractMetadata = contractNode["metadata"]!.AsObject();
            if (contractMetadata["id"]!.GetValue<string>() != capability.Id.Value || contractMetadata["version"]!.GetValue<string>() != capability.Version.ToString() || contractMetadata["category"]!.GetValue<string>() != capability.Category)
                throw Failure(BundleVerificationFailureKind.InvalidManifest, "capability-contract-identity-mismatch", $"Capability contract '{capability.Id}' identity does not match the manifest.");
            var contractDirectory = Path.GetDirectoryName(capability.ContractPath)?.Replace('\\', '/') ?? string.Empty;
            foreach (var section in new[] { "request", "response" })
            {
                var relativeSchema = contractNode[section]!["schema"]!.GetValue<string>();
                var schemaPath = string.IsNullOrEmpty(contractDirectory) ? relativeSchema : contractDirectory + "/" + relativeSchema;
                _ = ParseJson(ReadEntryAsync(Required(entries, schemaPath), CancellationToken.None).GetAwaiter().GetResult(), schemaPath);
            }
        }
    }

    private static void VerifyArtifacts(ModuleManifest manifest, IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        foreach (var artifact in manifest.RuntimeArtifacts)
        {
            var bytes = ReadEntryAsync(Required(entries, artifact.EntryPoint), CancellationToken.None).GetAwaiter().GetResult();
            if (!FixedEquals(CanonicalBundleContent.Sha256(bytes), artifact.Digest)) throw Failure(BundleVerificationFailureKind.ArtifactMismatch, "artifact-digest-mismatch", $"Artifact digest mismatch for '{artifact.Id}'.");
        }
    }

    private static async Task VerifyContributionDocumentsAsync(ModuleManifest manifest, IReadOnlyDictionary<string, ZipArchiveEntry> entries, CancellationToken cancellationToken)
    {
        foreach (var path in manifest.EventPublications.Select(value => value.SchemaPath)
                     .Concat(manifest.EventSubscriptions.Select(value => value.SchemaPath))
                     .Concat(manifest.UiComponents.SelectMany(value => new[] { value.PropertiesSchemaPath, value.EventsSchemaPath }))
                     .Distinct(StringComparer.Ordinal))
        {
            var schema = await ReadEntryAsync(Required(entries, path), cancellationToken).ConfigureAwait(false);
            _ = JsonSchema.FromText(System.Text.Encoding.UTF8.GetString(schema), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        }
        foreach (var path in manifest.PipelineDefinitionPaths.Distinct(StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(path);
            if (extension is not (".json" or ".yaml" or ".yml"))
                throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-extension-invalid", $"Pipeline definition '{path}' must be JSON or YAML.");
            var definition = await ParseStructuredEntryAsync(Required(entries, path), extension, cancellationToken).ConfigureAwait(false);
            await VerifyPipelineDefinitionAsync(path, definition, entries, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task VerifyPipelineDefinitionAsync(string path, JsonNode document, IReadOnlyDictionary<string, ZipArchiveEntry> entries, CancellationToken cancellationToken)
    {
        var root = document.AsObject();
        if (root["apiVersion"]?.GetValue<string>() != "pipelines.murchalka.dev/v1" || root["kind"]?.GetValue<string>() != "PipelineDefinition")
            throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' has an unsupported kind or API version.");
        var metadata = root["metadata"]?.AsObject() ?? throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' has no metadata.");
        try { _ = new CapabilityId(metadata["id"]?.GetValue<string>() ?? string.Empty); }
        catch (ArgumentException) { throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' has an invalid id."); }
        if (metadata["version"]?.GetValue<int>() is not > 0)
            throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' has an invalid version.");
        if (root["stages"] is not JsonArray { Count: > 0 } stages)
            throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' has no stages.");
        var stageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stageNode in stages)
        {
            var stage = stageNode?.AsObject() ?? throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' contains an invalid stage.");
            var id = stage["id"]?.GetValue<string>() ?? string.Empty;
            var mode = stage["mode"]?.GetValue<string>();
            if (!stageIds.Add(id) || mode is not ("sequential" or "parallelMerge" or "firstSuccessful" or "exactlyOne" or "fanOut" or "reduce"))
                throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' contains a duplicate stage or unsupported mode.");
        }
        foreach (var section in new[] { "input", "output" })
        {
            var schema = root[section]?.AsObject()?["schema"]?.GetValue<string>()
                ?? throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' has no {section} schema.");
            var schemaPath = ResolveBundleRelative(path, schema);
            var bytes = await ReadEntryAsync(Required(entries, schemaPath), cancellationToken).ConfigureAwait(false);
            _ = JsonSchema.FromText(System.Text.Encoding.UTF8.GetString(bytes), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        }
        var semantics = root["semantics"]?.AsObject() ?? throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' has no semantics.");
        if (semantics["deadline"]?.GetValue<string>() is not { Length: > 1 } ||
            semantics["cancellation"]?.GetValue<string>() is not ("required" or "optional") ||
            semantics["checkpointing"]?.GetValue<string>() is not ("required" or "optional" or "disabled"))
            throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-definition-invalid", $"Pipeline definition '{path}' has invalid execution semantics.");
    }

    private static string ResolveBundleRelative(string ownerPath, string relativePath)
    {
        if (relativePath.StartsWith('/') || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
            throw Failure(BundleVerificationFailureKind.InvalidManifest, "pipeline-schema-path-invalid", $"Pipeline schema path '{relativePath}' is unsafe.");
        var directory = Path.GetDirectoryName(ownerPath)?.Replace('\\', '/');
        return string.IsNullOrEmpty(directory) ? relativePath : directory + "/" + relativePath;
    }

    private static void VerifySupplyChainEntries(IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.Keys.Any(path => path.StartsWith("sbom/", StringComparison.Ordinal)) || !entries.Keys.Any(path => path.StartsWith("provenance/", StringComparison.Ordinal)))
            throw Failure(BundleVerificationFailureKind.InvalidArchive, "supply-chain-metadata-missing", "Bundle must include SBOM and provenance payloads.");
    }

    private static async Task<JsonNode> ParseStructuredEntryAsync(ZipArchiveEntry entry, string extension, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "murchalka-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "document" + extension);
        try
        {
            await File.WriteAllBytesAsync(path, await ReadEntryAsync(entry, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return StructuredDocument.Load(path);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static JsonNode ParseJson(byte[] bytes, string name) => JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 }) ?? throw new JsonException($"'{name}' is empty.");
    private static async Task<byte[]> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Length > RuntimeConstants.MaximumBundleBytes) throw new InvalidDataException($"Entry '{entry.FullName}' exceeds the size limit.");
        await using var input = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static ZipArchiveEntry Required(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string path) => entries.TryGetValue(path, out var entry) ? entry : throw new InvalidDataException($"Required bundle entry '{path}' is missing.");
    private static bool IsDigest(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool FixedEquals(string left, string right) => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(left), System.Text.Encoding.ASCII.GetBytes(right));
    private static string CurrentOs() => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "unknown";
    private static string CurrentArchitecture() => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() switch { "x64" => "x64", "arm64" => "arm64", var value => value };
    private static BundleVerificationException Failure(BundleVerificationFailureKind kind, string code, string message) => new(kind, code, message);
}
