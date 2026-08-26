using System.Collections.Concurrent;
using System.Threading.Channels;
using Murchalka.Runtime.Contracts.Common;

namespace Murchalka.Runtime.ModuleDiscovery.Watchers;

/// <summary>Discovers complete bundle files and moves them atomically into staging.</summary>
public sealed class ModuleDirectoryWatcher : IAsyncDisposable
{
    private readonly RuntimePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly int _stableObservations;
    private readonly Channel<string> _staged = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _operationsGate = new();
    private readonly List<Task> _operations = [];
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>Initializes an inbox watcher.</summary>
    /// <param name="paths">The Runtime paths.</param>
    /// <param name="timeProvider">The optional trusted time provider.</param>
    /// <param name="pollInterval">The stability polling interval.</param>
    /// <param name="stableObservations">The required consecutive stable observations.</param>
    public ModuleDirectoryWatcher(RuntimePaths paths, TimeProvider? timeProvider = null, TimeSpan? pollInterval = null, int stableObservations = 3)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _stableObservations = stableObservations >= 2 ? stableObservations : throw new ArgumentOutOfRangeException(nameof(stableObservations));
        _paths.EnsureCreated();
    }

    /// <summary>Starts filesystem observation and performs an initial inbox scan.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watcher is not null) throw new InvalidOperationException("Watcher is already started.");
        var watcher = new FileSystemWatcher(_paths.Inbox)
        {
            Filter = "*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024
        };
        watcher.Created += OnCandidate;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
        _watcher = watcher;
        watcher.EnableRaisingEvents = true;
        foreach (var path in Directory.EnumerateFiles(_paths.Inbox)) Queue(path);
    }

    /// <summary>Reads paths that were atomically moved into staging.</summary>
    /// <param name="cancellationToken">Stops enumeration.</param>
    /// <returns>An asynchronous sequence of staged bundle paths.</returns>
    public IAsyncEnumerable<string> ReadStagedAsync(CancellationToken cancellationToken) => _staged.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Gets whether bundle stabilization or staged delivery work remains pending.</summary>
    public bool HasPendingWork => !_pending.IsEmpty || _staged.Reader.Count > 0;

    private void OnCandidate(object sender, FileSystemEventArgs args) => Queue(args.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs args) => Queue(args.FullPath);
    private void OnError(object sender, ErrorEventArgs args)
    {
        if (_shutdown.IsCancellationRequested) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(_paths.Inbox)) Queue(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!_shutdown.IsCancellationRequested) _staged.Writer.TryComplete(exception);
        }
    }

    private void Queue(string path)
    {
        if (_shutdown.IsCancellationRequested || !IsCandidate(path)) return;
        var fullPath = Path.GetFullPath(path);
        if (!_pending.TryAdd(fullPath, 0)) return;
        var operation = StabilizeAndStageAsync(fullPath, _shutdown.Token);
        lock (_operationsGate) _operations.Add(operation);
        _ = RemoveWhenCompletedAsync(operation);
    }

    private async Task RemoveWhenCompletedAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        finally
        {
            lock (_operationsGate) _operations.Remove(operation);
        }
    }

    private async Task StabilizeAndStageAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            long lastLength = -1;
            DateTime lastWrite = default;
            var stable = 0;
            var deadline = _timeProvider.GetUtcNow().AddSeconds(30);
            while (_timeProvider.GetUtcNow() < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(path)) return;
                var info = new FileInfo(path);
                if (info.Length > 0 && info.Length == lastLength && info.LastWriteTimeUtc == lastWrite && CanReadExclusively(path)) stable++; else stable = 0;
                lastLength = info.Length;
                lastWrite = info.LastWriteTimeUtc;
                if (stable >= _stableObservations)
                {
                    var target = Path.Combine(_paths.Staging, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.murchalka");
                    File.Move(path, target);
                    await _staged.Writer.WriteAsync(target, cancellationToken).ConfigureAwait(false);
                    return;
                }
                await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _pending.TryRemove(path, out _); }
    }

    private static bool IsCandidate(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".murchalka", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) &&
               !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanReadExclusively(string path)
    {
        try { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return stream.Length > 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        var watcher = _watcher;
        _watcher = null;
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnCandidate;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnError;
            watcher.Dispose();
        }
        Task[] operations;
        lock (_operationsGate) operations = [.. _operations];
        await Task.WhenAll(operations).ConfigureAwait(false);
        _staged.Writer.TryComplete();
        _shutdown.Dispose();
    }
}
