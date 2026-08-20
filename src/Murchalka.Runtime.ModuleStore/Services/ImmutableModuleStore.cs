using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.ModuleStore.Internal;
using Murchalka.Runtime.RootSecurity.Manifests;

namespace Murchalka.Runtime.ModuleStore.Services;

/// <summary>Installs verified bundles into an immutable content-addressed filesystem store.</summary>
public sealed class ImmutableModuleStore : IModuleStore
{
    private readonly RuntimePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates an immutable module store.</summary>
    /// <param name="paths">The runtime filesystem paths.</param>
    /// <param name="timeProvider">The optional source of current time.</param>
    public ImmutableModuleStore(RuntimePaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paths.EnsureCreated();
    }

    /// <inheritdoc />
    public async Task<InstalledBundle> InstallAsync(VerifiedBundle bundle, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyArchiveUnchangedAsync(bundle, cancellationToken).ConfigureAwait(false);
            var digest = bundle.Identity.Digest[7..];
            var destination = Path.Combine(_paths.Installed, "sha256", digest);
            if (Directory.Exists(destination)) return await OpenRequiredAsync(bundle.Identity.Digest, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".installing-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(temporary);
            try
            {
                var archivePath = Path.Combine(temporary, "bundle.murchalka");
                await CopyDurableAsync(bundle.StagedPath, archivePath, cancellationToken).ConfigureAwait(false);
                var contentPath = Path.Combine(temporary, "content");
                Directory.CreateDirectory(contentPath);
                ExtractSafely(archivePath, contentPath);
                var metadataPath = Path.Combine(temporary, "metadata");
                Directory.CreateDirectory(metadataPath);
                await File.WriteAllTextAsync(Path.Combine(metadataPath, "manifest.json"), bundle.Manifest.Document.GetRawText(), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(metadataPath, "installed.json"), JsonSerializer.Serialize(new StoreMetadata(bundle.Identity.Digest, bundle.ArchiveDigest, _timeProvider.GetUtcNow())), cancellationToken).ConfigureAwait(false);
                MakePayloadReadOnly(contentPath, bundle.Manifest);
                File.SetAttributes(archivePath, File.GetAttributes(archivePath) | FileAttributes.ReadOnly);
                foreach (var metadataFile in Directory.EnumerateFiles(metadataPath)) File.SetAttributes(metadataFile, File.GetAttributes(metadataFile) | FileAttributes.ReadOnly);
                MakeDirectoriesReadOnly(temporary);
                Directory.Move(temporary, destination);
            }
            catch
            {
                if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
                throw;
            }
            return await OpenRequiredAsync(bundle.Identity.Digest, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<InstalledBundle?> OpenAsync(string digest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDigest(digest);
        var root = Path.Combine(_paths.Installed, "sha256", digest[7..]);
        if (!Directory.Exists(root)) return null;
        var manifestText = await File.ReadAllTextAsync(Path.Combine(root, "metadata", "manifest.json"), cancellationToken).ConfigureAwait(false);
        var metadataText = await File.ReadAllTextAsync(Path.Combine(root, "metadata", "installed.json"), cancellationToken).ConfigureAwait(false);
        var metadata = JsonSerializer.Deserialize<StoreMetadata>(metadataText) ?? throw new InvalidDataException("Installed store metadata is invalid.");
        if (!string.Equals(metadata.BundleDigest, digest, StringComparison.Ordinal)) throw new InvalidDataException("Installed store directory does not match its metadata digest.");
        var manifest = ManifestReader.Read(JsonNode.Parse(manifestText) ?? throw new InvalidDataException("Installed manifest is empty."));
        return new InstalledBundle(digest, Path.Combine(root, "bundle.murchalka"), Path.Combine(root, "content"), manifest, metadata.InstalledAt);
    }

    private async Task<InstalledBundle> OpenRequiredAsync(string digest, CancellationToken cancellationToken) =>
        await OpenAsync(digest, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException($"Installed bundle '{digest}' disappeared.");

    private static async Task VerifyArchiveUnchangedAsync(VerifiedBundle bundle, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(bundle.StagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(bundle.ArchiveDigest)))
            throw new BundleVerificationException(BundleVerificationFailureKind.HashMismatch, "staging-toctou", "Staged archive changed after verification.");
    }

    private static async Task CopyDurableAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(true);
    }

    private static void ExtractSafely(string archivePath, string contentPath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0) continue;
            var normalized = entry.FullName.Replace('\\', '/');
            var destination = Path.GetFullPath(Path.Combine(contentPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(Path.GetFullPath(contentPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException($"Archive entry '{entry.FullName}' escapes the content directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
            output.Flush(true);
        }
    }

    private static void MakePayloadReadOnly(string contentPath, ModuleManifest manifest)
    {
        var executablePaths = manifest.RuntimeArtifacts.Where(value => value.Mode == "process").Select(value => Path.GetFullPath(Path.Combine(contentPath, value.EntryPoint.Replace('/', Path.DirectorySeparatorChar)))).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(contentPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, executablePaths.Contains(Path.GetFullPath(file)) ? UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute : UnixFileMode.UserRead | UnixFileMode.GroupRead);
        }
    }

    private static void MakeDirectoriesReadOnly(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(value => value.Length))
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    private static void ValidateDigest(string digest)
    {
        if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ArgumentException("Invalid bundle digest.", nameof(digest));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

}
