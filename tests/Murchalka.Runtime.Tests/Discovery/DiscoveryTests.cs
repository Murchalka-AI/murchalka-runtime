using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.ModuleDiscovery.Watchers;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Discovery;

/// <summary>Verifies stable and atomic module inbox discovery.</summary>
public sealed class DiscoveryTests
{
    /// <summary>Verifies that a partial bundle is ignored until it is atomically renamed.</summary>
    [Fact]
    public async Task PartialFileIsIgnoredUntilAtomicRename()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        paths.EnsureCreated();
        await using var watcher = new ModuleDirectoryWatcher(paths, pollInterval: TimeSpan.FromMilliseconds(10), stableObservations: 2);
        watcher.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = watcher.ReadStagedAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var pending = Path.Combine(paths.Inbox, "hello.murchalka.partial");
        await File.WriteAllBytesAsync(pending, [1, 2, 3], timeout.Token);
        var next = enumerator.MoveNextAsync().AsTask();

        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(100, timeout.Token)));
        File.Move(pending, Path.Combine(paths.Inbox, "hello.murchalka"));
        Assert.True(await next);
        Assert.StartsWith(Path.GetFullPath(paths.Staging), Path.GetFullPath(enumerator.Current), StringComparison.Ordinal);
    }
}
