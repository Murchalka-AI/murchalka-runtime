using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Configuration.Internal;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Configuration;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Configuration.Services;

/// <summary>Stores schema-validated module configuration in atomic local files.</summary>
public sealed class FileModuleConfigurationStore : IModuleConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly RuntimePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a local configuration store.</summary>
    /// <param name="paths">The Runtime filesystem paths.</param>
    /// <param name="timeProvider">The optional trusted time source.</param>
    public FileModuleConfigurationStore(RuntimePaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paths.EnsureCreated();
    }

    /// <inheritdoc />
    public async Task<ModuleConfigurationSnapshot> GetAsync(InstalledBundle bundle, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadUnderGateAsync(bundle, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<ModuleConfigurationSnapshot> ReplaceAsync(
        InstalledBundle bundle,
        JsonElement values,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (values.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Module configuration must be a JSON object.");
        if (bundle.Manifest.Configuration is null) throw new InvalidOperationException("The module does not declare configuration.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadStoredAsync(bundle.Manifest.Id, cancellationToken).ConfigureAwait(false);
            var actualRevision = current?.Revision ?? 0;
            if (actualRevision != expectedRevision)
                throw new ConfigurationRevisionConflictException(expectedRevision, actualRevision);
            if (bundle.Manifest.Configuration.RestartPolicy == ConfigurationRestartPolicy.Immutable && actualRevision > 0)
                throw new InvalidOperationException("Immutable module configuration cannot be changed after its first revision.");

            var contract = LoadContract(bundle);
            var overrides = JsonNode.Parse(values.GetRawText())!.AsObject();
            var effective = Merge(contract.Defaults, overrides);
            Validate(contract.Schema, effective);
            var updated = new StoredConfiguration(
                bundle.Manifest.Id.Value,
                checked(actualRevision + 1),
                contract.SchemaDigest,
                JsonSerializer.SerializeToElement(overrides, SerializerOptions),
                _timeProvider.GetUtcNow());
            await WriteAtomicallyAsync(RecordPath(bundle.Manifest.Id), updated, cancellationToken).ConfigureAwait(false);
            return ToSnapshot(bundle.Manifest.Id, updated, effective);
        }
        finally { _gate.Release(); }
    }

    private async Task<ModuleConfigurationSnapshot> LoadUnderGateAsync(InstalledBundle bundle, CancellationToken cancellationToken)
    {
        var declaration = bundle.Manifest.Configuration;
        if (declaration is null)
            return new ModuleConfigurationSnapshot(bundle.Manifest.Id, 0, "sha256:" + new string('0', 64), EmptyObject(), bundle.InstalledAt);

        var contract = LoadContract(bundle);
        var stored = await LoadStoredAsync(bundle.Manifest.Id, cancellationToken).ConfigureAwait(false);
        var overrides = stored is null ? new JsonObject() : JsonNode.Parse(stored.Values.GetRawText())!.AsObject();
        var effective = Merge(contract.Defaults, overrides);
        Validate(contract.Schema, effective);
        var record = stored ?? new StoredConfiguration(bundle.Manifest.Id.Value, 0, contract.SchemaDigest, EmptyObject(), bundle.InstalledAt);
        return ToSnapshot(bundle.Manifest.Id, record with { SchemaDigest = contract.SchemaDigest }, effective);
    }

    private static ConfigurationContract LoadContract(InstalledBundle bundle)
    {
        var declaration = bundle.Manifest.Configuration ?? throw new InvalidOperationException("The module does not declare configuration.");
        var schemaPath = ResolveInside(bundle.ContentPath, declaration.SchemaPath);
        var schemaBytes = File.ReadAllBytes(schemaPath);
        var schema = JsonSchema.FromText(Encoding.UTF8.GetString(schemaBytes));
        var defaults = declaration.DefaultsPath is null
            ? new JsonObject()
            : JsonNode.Parse(File.ReadAllText(ResolveInside(bundle.ContentPath, declaration.DefaultsPath)))?.AsObject()
              ?? throw new InvalidDataException("Configuration defaults are empty.");
        return new ConfigurationContract(schema, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(schemaBytes)), defaults);
    }

    private static void Validate(JsonSchema schema, JsonObject values)
    {
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(values, SerializerOptions), new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });
        if (!result.IsValid) throw new InvalidDataException("Module configuration does not satisfy the signed configuration schema.");
    }

    private static JsonObject Merge(JsonObject defaults, JsonObject overrides)
    {
        var result = defaults.DeepClone().AsObject();
        MergeInto(result, overrides);
        return result;
    }

    private static void MergeInto(JsonObject target, JsonObject source)
    {
        foreach (var pair in source)
        {
            if (pair.Value is JsonObject sourceObject && target[pair.Key] is JsonObject targetObject)
                MergeInto(targetObject, sourceObject);
            else
                target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    private async Task<StoredConfiguration?> LoadStoredAsync(ModuleId moduleId, CancellationToken cancellationToken)
    {
        var path = RecordPath(moduleId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var stored = await JsonSerializer.DeserializeAsync<StoredConfiguration>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (stored is null || !string.Equals(stored.ModuleId, moduleId.Value, StringComparison.Ordinal))
            throw new InvalidDataException($"Configuration record for '{moduleId}' is invalid.");
        return stored;
    }

    private static async Task WriteAtomicallyAsync(string path, StoredConfiguration value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string RecordPath(ModuleId moduleId) => Path.Combine(_paths.ModuleConfiguration, moduleId.Value + ".json");

    private static string ResolveInside(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(normalizedRoot, StringComparison.Ordinal)) throw new InvalidDataException("Configuration artifact escapes bundle content.");
        if (!File.Exists(path)) throw new FileNotFoundException("Declared configuration artifact is missing.", path);
        return path;
    }

    private static ModuleConfigurationSnapshot ToSnapshot(ModuleId moduleId, StoredConfiguration stored, JsonObject effective) =>
        new(moduleId, stored.Revision, stored.SchemaDigest, JsonSerializer.SerializeToElement(effective, SerializerOptions), stored.UpdatedAt);

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

}
