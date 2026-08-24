using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Secrets;
using Murchalka.Runtime.Secrets.Internal;

namespace Murchalka.Runtime.Secrets.Services;

/// <summary>Persists secrets with AES-256-GCM encryption and atomic revision writes.</summary>
public sealed class EncryptedFileSecretStore : ISecretStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly RuntimePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _key;
    private bool _disposed;

    /// <summary>Creates an encrypted local secret store and its installation-scoped master key when absent.</summary>
    /// <param name="paths">The Runtime filesystem paths.</param>
    /// <param name="timeProvider">The optional trusted time source.</param>
    public EncryptedFileSecretStore(RuntimePaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paths.EnsureCreated();
        _key = LoadOrCreateKey(Path.Combine(_paths.Secrets, "master.key"));
    }

    /// <inheritdoc />
    public async Task<SecretMaterial?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateName(name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await ReadAsync(name, cancellationToken).ConfigureAwait(false);
            if (record is null) return null;
            var plaintext = new byte[record.Ciphertext.Length];
            using var cipher = new AesGcm(_key, 16);
            cipher.Decrypt(record.Nonce, record.Ciphertext, record.Tag, plaintext, Encoding.UTF8.GetBytes(record.Name));
            return new SecretMaterial(record.Name, record.Revision, plaintext);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<SecretVersion> PutAsync(string name, ReadOnlyMemory<byte> value, long expectedRevision, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        if (value.IsEmpty || value.Length > 64 * 1024) throw new ArgumentOutOfRangeException(nameof(value), "Secret size must be between 1 byte and 64 KiB.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(name, cancellationToken).ConfigureAwait(false);
            var actualRevision = current?.Revision ?? 0;
            if (actualRevision != expectedRevision)
                throw new InvalidOperationException($"Expected secret revision {expectedRevision}, but current revision is {actualRevision}.");
            var nonce = RandomNumberGenerator.GetBytes(12);
            var ciphertext = new byte[value.Length];
            var tag = new byte[16];
            using (var cipher = new AesGcm(_key, tag.Length))
                cipher.Encrypt(nonce, value.Span, ciphertext, tag, Encoding.UTF8.GetBytes(name));
            var updatedAt = _timeProvider.GetUtcNow();
            var record = new StoredSecret(name, checked(actualRevision + 1), nonce, ciphertext, tag, updatedAt);
            await WriteAtomicallyAsync(RecordPath(name), record, cancellationToken).ConfigureAwait(false);
            return new SecretVersion(name, record.Revision, updatedAt);
        }
        finally { _gate.Release(); }
    }

    private async Task<StoredSecret?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        var path = RecordPath(name);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var record = await JsonSerializer.DeserializeAsync<StoredSecret>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
        if (record is null || !string.Equals(record.Name, name, StringComparison.Ordinal)) throw new InvalidDataException("Encrypted secret record identity is invalid.");
        return record;
    }

    private static async Task WriteAtomicallyAsync(string path, StoredSecret record, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, record, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = Convert.FromBase64String(File.ReadAllText(path));
            if (existing.Length != 32) throw new InvalidDataException("The local secret master key has an invalid length.");
            return existing;
        }
        var key = RandomNumberGenerator.GetBytes(32);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, Convert.ToBase64String(key));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try { File.Move(temporary, path); }
        catch (IOException) when (File.Exists(path))
        {
            CryptographicOperations.ZeroMemory(key);
            File.Delete(temporary);
            return LoadOrCreateKey(path);
        }
        return key;
    }

    private string RecordPath(string name) => Path.Combine(_paths.Secrets, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name))) + ".secret.json");

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 256 || !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '/' or '-'))
            throw new ArgumentException("Secret name contains unsupported characters.", nameof(name));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        _gate.Dispose();
    }

}
