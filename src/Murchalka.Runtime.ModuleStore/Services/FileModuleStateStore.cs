using System.Text.Json;
using System.Text.Json.Serialization;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Lifecycle;
using Murchalka.Runtime.ModuleStore.Internal;

namespace Murchalka.Runtime.ModuleStore.Services;

/// <summary>Persists module lifecycle records and active or disabled state markers on disk.</summary>
public sealed class FileModuleStateStore : IModuleStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly RuntimePaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a file-backed module state store.</summary>
    /// <param name="paths">The runtime filesystem paths.</param>
    public FileModuleStateStore(RuntimePaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _paths.EnsureCreated();
    }

    /// <inheritdoc />
    public async Task<InstalledModuleRecord> SaveAsync(InstalledModuleRecord record, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var path = RecordPath(record.ModuleId);
            temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var document = StoredRecord.From(record);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
            temporary = null;
            UpdateMarker(record);
            return record;
        }
        finally
        {
            if (temporary is not null) TryDeleteTemporary(temporary);
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<InstalledModuleRecord?> GetAsync(ModuleId id, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = RecordPath(id);
            return File.Exists(path)
                ? await ReadRecordAsync(path, cancellationToken).ConfigureAwait(false)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledModuleRecord>> GetAllAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new List<InstalledModuleRecord>();
            foreach (var path in Directory.EnumerateFiles(_paths.State, "*.json").Order(StringComparer.Ordinal))
            {
                result.Add(await ReadRecordAsync(path, cancellationToken).ConfigureAwait(false));
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string RecordPath(ModuleId id) => Path.Combine(_paths.State, id.Value + ".json");

    private static async Task<InstalledModuleRecord> ReadRecordAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var stored = await JsonSerializer.DeserializeAsync<StoredRecord>(stream, Options, cancellationToken).ConfigureAwait(false);
        return stored?.ToRecord() ?? throw new InvalidDataException($"State record '{path}' is empty.");
    }

    private static void TryDeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void UpdateMarker(InstalledModuleRecord record)
    {
        var active = Path.Combine(_paths.Active, record.ModuleId.Value + ".json");
        var disabled = Path.Combine(_paths.Disabled, record.ModuleId.Value + ".json");
        if (File.Exists(active)) File.Delete(active);
        if (File.Exists(disabled)) File.Delete(disabled);
        var target = record.State == ModuleLifecycleState.Active ? active : record.State == ModuleLifecycleState.Disabled ? disabled : null;
        if (target is not null) File.WriteAllText(target, JsonSerializer.Serialize(new { bundleDigest = record.BundleDigest, version = record.Version.ToString(), revision = record.Revision }));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

}
