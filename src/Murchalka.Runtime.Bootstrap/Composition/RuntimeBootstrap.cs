using Murchalka.Runtime.Audit.Services;
using Murchalka.Runtime.Bindings.Services;
using Murchalka.Runtime.Bootstrap.Hosting;
using Murchalka.Runtime.Capabilities.Registry;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Dependencies.Locks;
using Murchalka.Runtime.Dependencies.Resolution;
using Murchalka.Runtime.Events.Delivery;
using Murchalka.Runtime.Events.Fabric;
using Murchalka.Runtime.Kernel.Services;
using Murchalka.Runtime.ModuleDiscovery.Watchers;
using Murchalka.Runtime.ModuleStore.Services;
using Murchalka.Runtime.ModuleSupervisor.Services;
using Murchalka.Runtime.Pipelines.Execution;
using Murchalka.Runtime.Pipelines.Registry;
using Murchalka.Runtime.RootSecurity.Bundles;
using Murchalka.Runtime.RootSecurity.Permissions;
using Murchalka.Runtime.RootSecurity.Trust;

namespace Murchalka.Runtime.Bootstrap.Composition;

/// <summary>Creates the product-neutral Runtime composition without product modules.</summary>
public static class RuntimeBootstrap
{
    /// <summary>Creates a fully composed Runtime application.</summary>
    /// <param name="root">The Runtime data root.</param>
    /// <param name="timeProvider">The optional trusted time provider.</param>
    /// <param name="discoveryPollInterval">The optional inbox stability polling interval.</param>
    /// <returns>The composed Runtime application.</returns>
    public static RuntimeApplication Create(string root, TimeProvider? timeProvider = null, TimeSpan? discoveryPollInterval = null)
    {
        var paths = new RuntimePaths(root);
        paths.EnsureCreated();
        var clock = timeProvider ?? TimeProvider.System;
        var audit = new HashChainedRootAudit(paths, clock);
        var trust = new TrustedKeyStore(paths);
        var verifier = new BundleVerifier(trust, clock);
        var store = new ImmutableModuleStore(paths, clock);
        var state = new FileModuleStateStore(paths);
        var grants = new PermissionGrantStore(paths, trust, clock);
        var supervisor = new ProcessModuleSupervisor(paths);
        var capabilities = new CapabilityRegistry(supervisor, audit);
        var bindings = new FileBindingStore(paths, "local");
        var resolver = new DependencyResolver();
        var locks = new CompositionLockStore(paths, clock);
        var pipelines = new DynamicPipelineRuntime(new ModulePipelineHandlerInvoker(supervisor), audit, clock);
        var events = new DurableEventFabric(paths, new ModuleEventDeliverySink(supervisor), audit, clock);
        var watcher = new ModuleDirectoryWatcher(paths, clock, discoveryPollInterval);
        var kernel = new RuntimeKernel(paths, watcher, verifier, store, state, grants, supervisor, capabilities, bindings, resolver, locks, pipelines, events, audit, clock);
        return new RuntimeApplication(paths, kernel, supervisor, store, state, bindings, audit);
    }
}
