using Murchalka.Runtime.Audit.Services;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Kernel.Services;
using Murchalka.Runtime.ModuleSupervisor.Services;

namespace Murchalka.Runtime.Bootstrap.Hosting;

/// <summary>Owns a composed Runtime kernel and its process-level resources.</summary>
public sealed class RuntimeApplication : IAsyncDisposable
{
    private readonly ProcessModuleSupervisor _supervisor;
    private readonly IModuleStore _moduleStore;
    private readonly IModuleStateStore _stateStore;
    private readonly HashChainedRootAudit _audit;
    private bool _disposed;

    /// <summary>Initializes an owned Runtime application.</summary>
    /// <param name="paths">The Runtime paths.</param>
    /// <param name="kernel">The composed Runtime kernel.</param>
    /// <param name="supervisor">The owned process supervisor.</param>
    /// <param name="moduleStore">The owned immutable module store.</param>
    /// <param name="stateStore">The owned module state store.</param>
    /// <param name="audit">The owned Root audit.</param>
    public RuntimeApplication(RuntimePaths paths, RuntimeKernel kernel, ProcessModuleSupervisor supervisor, IModuleStore moduleStore, IModuleStateStore stateStore, HashChainedRootAudit audit)
    {
        Paths = paths;
        Kernel = kernel;
        _supervisor = supervisor;
        _moduleStore = moduleStore;
        _stateStore = stateStore;
        _audit = audit;
    }

    /// <summary>Gets the Runtime data paths.</summary>
    public RuntimePaths Paths { get; }

    /// <summary>Gets the composed microkernel.</summary>
    public RuntimeKernel Kernel { get; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await Kernel.DisposeAsync().ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        _stateStore.Dispose();
        _moduleStore.Dispose();
        await _audit.DisposeAsync().ConfigureAwait(false);
    }
}
