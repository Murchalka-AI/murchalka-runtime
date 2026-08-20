using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Lifecycle;
using Murchalka.Runtime.ModuleStore.Services;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Persistence;

/// <summary>Verifies durable and concurrent module lifecycle persistence.</summary>
public sealed class ModuleStateStoreTests
{
    /// <summary>Verifies that concurrent readers do not prevent atomic state replacement.</summary>
    [Fact]
    public async Task ConcurrentReadsDoNotBlockAtomicStateReplacement()
    {
        using var directory = new TestDirectory();
        var paths = new RuntimePaths(directory.Path);
        using var store = new FileModuleStateStore(paths);
        var moduleId = new ModuleId("dev.murchalka.state-test");
        var record = new InstalledModuleRecord(moduleId, SemanticVersion.Parse("1.0.0"), "sha256:" + new string('a', 64), "dev.murchalka.tests",
            ModuleLifecycleState.Verifying, 1, DateTimeOffset.UtcNow, "initial", null, true);
        await store.SaveAsync(record, CancellationToken.None);

        using var stopReading = new CancellationTokenSource();
        var reader = Task.Run(() => ReadUntilCancelledAsync(store, moduleId, stopReading.Token));
        try
        {
            for (var revision = 2; revision <= 256; revision++)
            {
                record = record with { Revision = revision, UpdatedAt = DateTimeOffset.UtcNow };
                await store.SaveAsync(record, CancellationToken.None);
            }
        }
        finally
        {
            await stopReading.CancelAsync();
            await reader;
        }

        var persisted = await store.GetAsync(moduleId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(256, persisted.Revision);
    }

    private static async Task ReadUntilCancelledAsync(FileModuleStateStore store, ModuleId moduleId, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = await store.GetAsync(moduleId, cancellationToken);
                Assert.NotNull(record);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
