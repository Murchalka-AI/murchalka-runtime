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
    private readonly IDisposable _bindingStore;
    private readonly IDisposable _configurationStore;
    private readonly IDisposable _secretStore;
    private readonly IDisposable _migrationCoordinator;
    private readonly HashChainedRootAudit _audit;
    private bool _disposed;

    /// <summary>Initializes an owned Runtime application.</summary>
    /// <param name="paths">The Runtime paths.</param>
    /// <param name="kernel">The composed Runtime kernel.</param>
    /// <param name="supervisor">The owned process supervisor.</param>
    /// <param name="moduleStore">The owned immutable module store.</param>
    /// <param name="stateStore">The owned module state store.</param>
    /// <param name="bindingStore">The owned administrative binding store.</param>
    /// <param name="configurationStore">The owned module configuration store.</param>
    /// <param name="secretStore">The owned encrypted secret store.</param>
    /// <param name="migrationCoordinator">The owned state migration coordinator.</param>
    /// <param name="audit">The owned Root audit.</param>
    public RuntimeApplication(RuntimePaths paths, RuntimeKernel kernel, ProcessModuleSupervisor supervisor, IModuleStore moduleStore, IModuleStateStore stateStore, IDisposable bindingStore, IDisposable configurationStore, IDisposable secretStore, IDisposable migrationCoordinator, HashChainedRootAudit audit)
    {
        Paths = paths;
        Kernel = kernel;
        _supervisor = supervisor;
        _moduleStore = moduleStore;
        _stateStore = stateStore;
        _bindingStore = bindingStore;
        _configurationStore = configurationStore;
        _secretStore = secretStore;
        _migrationCoordinator = migrationCoordinator;
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
        _bindingStore.Dispose();
        _configurationStore.Dispose();
        _secretStore.Dispose();
        _migrationCoordinator.Dispose();
        _moduleStore.Dispose();
        await _audit.DisposeAsync().ConfigureAwait(false);
    }
}
