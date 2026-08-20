using System.Collections.Concurrent;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Dependencies;
using Murchalka.Runtime.Contracts.Lifecycle;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.ModuleDiscovery.Watchers;

namespace Murchalka.Runtime.Kernel.Services;

/// <summary>Coordinates secure module discovery, verification, installation, activation, recovery, and lifecycle transitions.</summary>
public sealed class RuntimeKernel : IAsyncDisposable
{
    private static readonly Dictionary<ModuleLifecycleState, IReadOnlySet<ModuleLifecycleState>> AllowedTransitions =
        new Dictionary<ModuleLifecycleState, IReadOnlySet<ModuleLifecycleState>>
        {
            [ModuleLifecycleState.Verifying] = Set(ModuleLifecycleState.AwaitingTrust, ModuleLifecycleState.Resolving, ModuleLifecycleState.Quarantined, ModuleLifecycleState.Failed),
            [ModuleLifecycleState.AwaitingTrust] = Set(ModuleLifecycleState.Verifying, ModuleLifecycleState.Disabled),
            [ModuleLifecycleState.Resolving] = Set(ModuleLifecycleState.PendingDependencies, ModuleLifecycleState.PendingBinding, ModuleLifecycleState.PendingPermission, ModuleLifecycleState.Incompatible, ModuleLifecycleState.Conflict, ModuleLifecycleState.Installing, ModuleLifecycleState.Failed),
            [ModuleLifecycleState.PendingPermission] = Set(ModuleLifecycleState.Verifying, ModuleLifecycleState.Disabled),
            [ModuleLifecycleState.PendingDependencies] = Set(ModuleLifecycleState.Verifying, ModuleLifecycleState.Disabled),
            [ModuleLifecycleState.PendingBinding] = Set(ModuleLifecycleState.Verifying, ModuleLifecycleState.Disabled),
            [ModuleLifecycleState.Incompatible] = Set(ModuleLifecycleState.Verifying, ModuleLifecycleState.Disabled),
            [ModuleLifecycleState.Conflict] = Set(ModuleLifecycleState.Verifying, ModuleLifecycleState.Disabled),
            [ModuleLifecycleState.Installing] = Set(ModuleLifecycleState.Starting, ModuleLifecycleState.Disabled, ModuleLifecycleState.Failed),
            [ModuleLifecycleState.Starting] = Set(ModuleLifecycleState.HealthChecking, ModuleLifecycleState.Failed),
            [ModuleLifecycleState.HealthChecking] = Set(ModuleLifecycleState.Active, ModuleLifecycleState.Failed),
            [ModuleLifecycleState.Active] = Set(ModuleLifecycleState.Draining, ModuleLifecycleState.Failed, ModuleLifecycleState.Updating),
            [ModuleLifecycleState.Draining] = Set(ModuleLifecycleState.Disabled, ModuleLifecycleState.Failed),
            [ModuleLifecycleState.Disabled] = Set(ModuleLifecycleState.Starting, ModuleLifecycleState.Verifying, ModuleLifecycleState.PendingPermission, ModuleLifecycleState.Uninstalled),
            [ModuleLifecycleState.Failed] = Set(ModuleLifecycleState.Starting, ModuleLifecycleState.Verifying, ModuleLifecycleState.PendingPermission, ModuleLifecycleState.Disabled, ModuleLifecycleState.Quarantined),
            [ModuleLifecycleState.Updating] = Set(ModuleLifecycleState.Active, ModuleLifecycleState.Failed),
            [ModuleLifecycleState.Quarantined] = Set(ModuleLifecycleState.Verifying),
            [ModuleLifecycleState.Uninstalled] = Set(ModuleLifecycleState.Verifying)
        };

    private readonly RuntimePaths _paths;
    private readonly ModuleDirectoryWatcher _watcher;
    private readonly IBundleVerifier _verifier;
    private readonly IModuleStore _store;
    private readonly IModuleStateStore _state;
    private readonly IPermissionGrantStore _grants;
    private readonly IModuleSupervisor _supervisor;
    private readonly ICapabilityRegistry _capabilities;
    private readonly IBindingStore _bindings;
    private readonly IDependencyResolver _resolver;
    private readonly ICompositionLockStore _locks;
    private readonly IRootAudit _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _moduleGates = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _reconciliation = new(1, 1);
    private Task? _processing;
    private bool _started;

    /// <summary>Creates the runtime orchestration kernel.</summary>
    /// <param name="paths">The runtime filesystem paths.</param>
    /// <param name="watcher">The module inbox watcher.</param>
    /// <param name="verifier">The bundle verifier.</param>
    /// <param name="store">The immutable module store.</param>
    /// <param name="state">The module lifecycle state store.</param>
    /// <param name="grants">The permission grant store.</param>
    /// <param name="supervisor">The module process supervisor.</param>
    /// <param name="capabilities">The runtime capability registry.</param>
    /// <param name="bindings">The scoped administrative binding store.</param>
    /// <param name="resolver">The dependency resolver.</param>
    /// <param name="locks">The generated composition lock store.</param>
    /// <param name="audit">The root audit trail.</param>
    /// <param name="timeProvider">The optional source of current time.</param>
    public RuntimeKernel(RuntimePaths paths, ModuleDirectoryWatcher watcher, IBundleVerifier verifier, IModuleStore store, IModuleStateStore state,
        IPermissionGrantStore grants, IModuleSupervisor supervisor, ICapabilityRegistry capabilities, IBindingStore bindings,
        IDependencyResolver resolver, ICompositionLockStore locks, IRootAudit audit, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _supervisor.ModuleExited += OnModuleExited;
    }

    /// <summary>Gets the registry used to invoke active module capabilities.</summary>
    public ICapabilityRegistry Capabilities => _capabilities;

    /// <summary>Starts recovery and continuous module inbox processing.</summary>
    /// <param name="cancellationToken">A token that cancels startup.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("Runtime kernel is already started.");
        _started = true;
        _paths.EnsureCreated();
        await _audit.AppendAsync("runtime.started", "runtime", "success", "zero-module-capable", new Dictionary<string, string?> { ["version"] = RuntimeConstants.Version.ToString() }, cancellationToken).ConfigureAwait(false);
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        _watcher.Start();
        _processing = ProcessInboxAsync(_shutdown.Token);
    }

    /// <summary>Gets the persisted status of every known module.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The module statuses ordered by module identifier.</returns>
    public async Task<IReadOnlyList<ModuleStatus>> GetStatusAsync(CancellationToken cancellationToken = default) =>
        (await _state.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(ToStatus).OrderBy(value => value.ModuleId, StringComparer.Ordinal).ToArray();

    /// <summary>Gets the current validated administrative bindings.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The current binding document.</returns>
    public Task<BindingDocument> GetBindingsAsync(CancellationToken cancellationToken = default) => _bindings.GetAsync(cancellationToken);

    /// <summary>Atomically replaces administrative bindings and reconciles pending modules.</summary>
    /// <param name="document">The untrusted binding document.</param>
    /// <param name="expectedRevision">The revision observed by the administrator.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The validated stored binding document.</returns>
    public async Task<BindingDocument> ReplaceBindingsAsync(JsonElement document, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var updated = await _bindings.ReplaceAsync(document, expectedRevision, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync("bindings.revised", updated.Installation, "success", "binding-revision-committed", new Dictionary<string, string?>
        {
            ["priorRevision"] = expectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["revision"] = updated.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["bindingCount"] = updated.Bindings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        await ReconcileActiveDependenciesAsync(excludedModule: null, cancellationToken).ConfigureAwait(false);
        await ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>Enables or retries activation of a known module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The updated module status, or <see langword="null"/> when the module is unknown.</returns>
    public async Task<ModuleStatus?> EnableAsync(ModuleId moduleId, CancellationToken cancellationToken = default)
    {
        var gate = Gate(moduleId.Value);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
            if (record is null) return null;
            if (record.State == ModuleLifecycleState.Active) return ToStatus(record);
            var retained = RetainedStagingPath(record.BundleDigest);
            if (File.Exists(retained))
            {
                await ProcessStagedUnderGateAsync(retained, cancellationToken).ConfigureAwait(false);
                return (await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false)) is { } updated ? ToStatus(updated) : null;
            }
            var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Installed bundle is missing.");
            var verified = await _verifier.VerifyAsync(installed.BundlePath, cancellationToken).ConfigureAwait(false);
            if (record.State != ModuleLifecycleState.Verifying)
                record = await TransitionAsync(record, ModuleLifecycleState.Verifying, "enable-requested", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
            var enabled = await ResolveAndActivateAsync(record, installed, verified, forceActivation: true, cancellationToken).ConfigureAwait(false);
            return ToStatus(enabled);
        }
        finally { gate.Release(); }
    }

    /// <summary>Drains and disables a known module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The updated module status, or <see langword="null"/> when the module is unknown.</returns>
    public async Task<ModuleStatus?> DisableAsync(ModuleId moduleId, CancellationToken cancellationToken = default)
    {
        var gate = Gate(moduleId.Value);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
            if (record is null) return null;
            if (record.State == ModuleLifecycleState.Disabled) return ToStatus(record);
            if (record.State == ModuleLifecycleState.Active && record.InstanceId is not null)
            {
                var instance = new InstanceId(record.InstanceId);
                var draining = await TransitionAsync(record, ModuleLifecycleState.Draining, "disable-requested", desiredEnabled: false, cancellationToken).ConfigureAwait(false);
                _capabilities.Unregister(record.ModuleId, instance);
                await _supervisor.StopAsync(instance, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                record = await TransitionAsync(draining, ModuleLifecycleState.Disabled, "disabled", desiredEnabled: false, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                record = await TransitionAsync(record, ModuleLifecycleState.Disabled, "disabled-before-active", desiredEnabled: false, cancellationToken).ConfigureAwait(false);
            }
            await ReconcileActiveDependenciesAsync(moduleId, cancellationToken).ConfigureAwait(false);
            return ToStatus(record);
        }
        finally { gate.Release(); }
    }

    private async Task ProcessInboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var staged in _watcher.ReadStagedAsync(cancellationToken).ConfigureAwait(false))
                await ProcessStagedAsync(staged, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ProcessStagedAsync(string stagedPath, CancellationToken cancellationToken)
    {
        await _audit.AppendAsync("bundle.discovered", Path.GetFileName(stagedPath), "success", "stable-and-staged", cancellationToken: cancellationToken).ConfigureAwait(false);
        VerifiedBundle verified;
        try { verified = await _verifier.VerifyAsync(stagedPath, cancellationToken).ConfigureAwait(false); }
        catch (BundleTrustRequiredException exception)
        {
            var gate = Gate(exception.Candidate.Manifest.Id.Value);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await RecordAwaitingTrustAsync(exception.Candidate, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
            return;
        }
        catch (BundleVerificationException exception)
        {
            await QuarantineAsync(stagedPath, exception.Code, exception.Message, cancellationToken).ConfigureAwait(false);
            return;
        }
        var moduleGate = Gate(verified.Manifest.Id.Value);
        await moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ProcessVerifiedUnderGateAsync(verified, cancellationToken).ConfigureAwait(false); }
        finally { moduleGate.Release(); }
        await ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessStagedUnderGateAsync(string stagedPath, CancellationToken cancellationToken)
    {
        try
        {
            var verified = await _verifier.VerifyAsync(stagedPath, cancellationToken).ConfigureAwait(false);
            await ProcessVerifiedUnderGateAsync(verified, cancellationToken).ConfigureAwait(false);
        }
        catch (BundleTrustRequiredException exception) { await RecordAwaitingTrustAsync(exception.Candidate, cancellationToken).ConfigureAwait(false); }
        catch (BundleVerificationException exception) { await QuarantineAsync(stagedPath, exception.Code, exception.Message, cancellationToken).ConfigureAwait(false); }
    }

    private async Task ProcessVerifiedUnderGateAsync(VerifiedBundle verified, CancellationToken cancellationToken)
    {
        var existing = await _state.GetAsync(verified.Manifest.Id, cancellationToken).ConfigureAwait(false);
        if (existing is { State: ModuleLifecycleState.Active } && existing.BundleDigest == verified.Identity.Digest)
        {
            DeleteIfStaged(verified.StagedPath);
            await _audit.AppendAsync("bundle.duplicate", verified.Manifest.Id.Value, "ignored", "already-active", new Dictionary<string, string?> { ["digest"] = verified.Identity.Digest }, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (existing is not null && existing.BundleDigest != verified.Identity.Digest && existing.State != ModuleLifecycleState.Uninstalled)
        {
            await QuarantineAsync(verified.StagedPath, "upgrade-requires-side-by-side-phase", "Replacing an installed bundle requires the side-by-side upgrade phase.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var record = existing ?? InstalledModuleRecord.Create(verified, _timeProvider.GetUtcNow());
        if (existing is null) record = await SaveAndAuditAsync(record, "bundle.verified", "signature-and-content-valid", cancellationToken).ConfigureAwait(false);
        else record = await TransitionAsync(record, ModuleLifecycleState.Verifying, "reverification-requested", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        var installed = await _store.InstallAsync(verified, cancellationToken).ConfigureAwait(false);
        DeleteIfStaged(verified.StagedPath);
        await ResolveAndActivateAsync(record, installed, verified, forceActivation: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstalledModuleRecord> ResolveAndActivateAsync(
        InstalledModuleRecord record,
        InstalledBundle installed,
        VerifiedBundle verified,
        bool forceActivation,
        CancellationToken cancellationToken)
    {
        record = await TransitionAsync(record, ModuleLifecycleState.Resolving, "dependency-resolution-started", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        var resolution = await ResolveDependenciesAsync(installed, cancellationToken).ConfigureAwait(false);
        if (!resolution.Succeeded)
            return await TransitionAsync(record, ToLifecycleState(resolution.State), resolution.ReasonCode, desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        var grant = await _grants.EvaluateAsync(verified, cancellationToken).ConfigureAwait(false);
        if (!grant.Granted)
            return await TransitionAsync(record, ModuleLifecycleState.PendingPermission, grant.ReasonCode, desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        record = await TransitionAsync(record, ModuleLifecycleState.Installing, "composition-lock-generating", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        var lockPath = await _locks.WriteAsync(installed, resolution, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync("composition.locked", installed.Manifest.Id.Value, "success", "dependencies-resolved", new Dictionary<string, string?>
        {
            ["lock"] = Path.GetFileName(lockPath),
            ["bindingRevision"] = resolution.CapabilityDependencies.Select(value => value.BindingRevision).DefaultIfEmpty(0).Max().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["dependencyCount"] = (resolution.ModuleDependencies.Count + resolution.CapabilityDependencies.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        if (!forceActivation && installed.Manifest.Activation.Mode == "manual")
            return await TransitionAsync(record, ModuleLifecycleState.Disabled, "manual-activation-required", desiredEnabled: false, cancellationToken).ConfigureAwait(false);
        return await ActivateAsync(record, installed, grant, resolution, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InstalledModuleRecord> ActivateAsync(InstalledModuleRecord record, InstalledBundle installed, PermissionDecision grant, DependencyResolutionResult resolution, CancellationToken cancellationToken)
    {
        IModuleGatewaySession? session = null;
        try
        {
            record = await TransitionAsync(record, ModuleLifecycleState.Starting, "process-starting", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
            session = await _supervisor.StartAsync(installed, grant, CreateDependencySnapshot(installed, resolution), cancellationToken).ConfigureAwait(false);
            record = await TransitionAsync(record with { InstanceId = session.InstanceId.Value }, ModuleLifecycleState.HealthChecking, "protocol-authenticated", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
            ModuleHealth? health = null;
            for (var attempt = 0; attempt < installed.Manifest.Health.ReadinessFailureThreshold; attempt++)
            {
                health = await session.ProbeHealthAsync(installed.Manifest.Health.ReadinessTimeout, cancellationToken).ConfigureAwait(false);
                if (health.Status == ModuleHealthStatus.Ready) break;
            }
            if (health?.Status != ModuleHealthStatus.Ready) throw new ModuleActivationException("readiness-failed", "Module did not pass readiness health gate.");
            var activation = await session.SendControlAsync(ControlMessageKind.Activate, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!activation.Succeeded) throw new ModuleActivationException("activation-control-rejected", $"Module activation failed: {activation.ErrorCode}.");
            _capabilities.Register(installed.Manifest, session.InstanceId, installed.ContentPath, installed.Digest);
            return await TransitionAsync(record, ModuleLifecycleState.Active, "health-gate-passed", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (session is not null)
            {
                _capabilities.Unregister(record.ModuleId, session.InstanceId);
                await _supervisor.StopAsync(session.InstanceId, installed.Manifest.Activation.DrainTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            return await TransitionAsync(record, ModuleLifecycleState.Failed, FailureCode(exception), desiredEnabled: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<DependencyResolutionResult> ResolveDependenciesAsync(InstalledBundle consumer, CancellationToken cancellationToken)
    {
        var records = await _state.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var modules = new List<AvailableModule>();
        foreach (var record in records.Where(value => value.State is not (ModuleLifecycleState.Uninstalled or ModuleLifecycleState.Quarantined)))
        {
            var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false);
            if (installed is not null)
                modules.Add(new AvailableModule(installed.Manifest, installed.Digest, record.State == ModuleLifecycleState.Active));
        }
        if (modules.All(value => value.BundleDigest != consumer.Digest))
            modules.Add(new AvailableModule(consumer.Manifest, consumer.Digest, false));
        var bindings = await _bindings.GetAsync(cancellationToken).ConfigureAwait(false);
        return _resolver.Resolve(new DependencyResolutionRequest(
            consumer.Manifest,
            consumer.Digest,
            modules,
            _capabilities.Snapshot(),
            bindings,
            BindingScopeContext.ForModule(consumer.Manifest.Id),
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
    }

    private static DependencyEndpointsSnapshot CreateDependencySnapshot(InstalledBundle consumer, DependencyResolutionResult resolution)
    {
        var revision = resolution.CapabilityDependencies.Select(value => value.BindingRevision).DefaultIfEmpty(0).Max();
        var endpoints = resolution.CapabilityDependencies.Select(value => new DependencyEndpoint(
            value.RequirementId,
            value.ProviderModule,
            value.ProviderModuleVersion,
            value.CapabilityId,
            value.CapabilityVersion,
            value.RuntimeInstance,
            new Uri($"murchalka://runtime/capabilities/{Uri.EscapeDataString(value.CapabilityId.Value)}/{Uri.EscapeDataString(value.RuntimeInstance.Value)}"),
            $"binding:{consumer.Manifest.Id.Value}:{value.RequirementId}:{value.BindingRevision}"))
            .ToArray();
        return new DependencyEndpointsSnapshot(revision, endpoints);
    }

    private static ModuleLifecycleState ToLifecycleState(DependencyResolutionState state) => state switch
    {
        DependencyResolutionState.PendingDependencies => ModuleLifecycleState.PendingDependencies,
        DependencyResolutionState.PendingBinding => ModuleLifecycleState.PendingBinding,
        DependencyResolutionState.PendingPermission => ModuleLifecycleState.PendingPermission,
        DependencyResolutionState.Incompatible => ModuleLifecycleState.Incompatible,
        DependencyResolutionState.Conflict => ModuleLifecycleState.Conflict,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A successful resolution has no pending lifecycle state.")
    };

    private async Task ReconcileActiveDependenciesAsync(ModuleId? excludedModule, CancellationToken cancellationToken)
    {
        var active = (await _state.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(value => value.State == ModuleLifecycleState.Active && value.DesiredEnabled && value.ModuleId != excludedModule)
            .OrderBy(value => value.ModuleId.Value, StringComparer.Ordinal)
            .ToArray();
        foreach (var snapshot in active)
        {
            var gate = Gate(snapshot.ModuleId.Value);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var record = await _state.GetAsync(snapshot.ModuleId, cancellationToken).ConfigureAwait(false);
                if (record is not { State: ModuleLifecycleState.Active, DesiredEnabled: true, InstanceId: not null }) continue;
                var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false);
                if (installed is null) continue;
                var resolution = await ResolveDependenciesAsync(installed, cancellationToken).ConfigureAwait(false);
                if (!resolution.Succeeded)
                {
                    var instance = new InstanceId(record.InstanceId);
                    var draining = await TransitionAsync(record, ModuleLifecycleState.Draining, "required-dependency-changed", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
                    _capabilities.Unregister(record.ModuleId, instance);
                    await _supervisor.StopAsync(instance, installed.Manifest.Activation.DrainTimeout, cancellationToken).ConfigureAwait(false);
                    var disabled = await TransitionAsync(draining, ModuleLifecycleState.Disabled, "dependency-reconciliation", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
                    var verifying = await TransitionAsync(disabled, ModuleLifecycleState.Verifying, "dependency-reconciliation", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
                    var resolving = await TransitionAsync(verifying, ModuleLifecycleState.Resolving, "dependency-reconciliation", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
                    await TransitionAsync(resolving, ToLifecycleState(resolution.State), resolution.ReasonCode, desiredEnabled: true, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var session = _supervisor.GetSession(new InstanceId(record.InstanceId));
                if (session is null) continue;
                var update = await session.UpdateDependenciesAsync(CreateDependencySnapshot(installed, resolution), TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                if (!update.Succeeded) throw new InvalidOperationException($"Module rejected dependency update: {update.ErrorCode}.");
                var lockPath = await _locks.WriteAsync(installed, resolution, cancellationToken).ConfigureAwait(false);
                await _audit.AppendAsync("bindings.applied", record.ModuleId.Value, "success", "dependency-route-switched", new Dictionary<string, string?>
                {
                    ["lock"] = Path.GetFileName(lockPath),
                    ["bindingRevision"] = resolution.CapabilityDependencies.Select(value => value.BindingRevision).DefaultIfEmpty(0).Max().ToString(System.Globalization.CultureInfo.InvariantCulture)
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }

    private async Task ReconcilePendingAsync(CancellationToken cancellationToken)
    {
        if (!await _reconciliation.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            var progress = true;
            while (progress)
            {
                progress = false;
                var pending = (await _state.GetAllAsync(cancellationToken).ConfigureAwait(false))
                    .Where(value => value.DesiredEnabled && value.State is ModuleLifecycleState.PendingDependencies or ModuleLifecycleState.PendingBinding or ModuleLifecycleState.PendingPermission or ModuleLifecycleState.Incompatible or ModuleLifecycleState.Conflict)
                    .OrderBy(value => value.ModuleId.Value, StringComparer.Ordinal)
                    .ToArray();
                foreach (var record in pending)
                {
                    var updated = await EnableAsync(record.ModuleId, cancellationToken).ConfigureAwait(false);
                    progress |= updated?.State == ModuleLifecycleState.Active;
                }
            }
        }
        finally { _reconciliation.Release(); }
    }

    private async Task RecordAwaitingTrustAsync(VerifiedBundle candidate, CancellationToken cancellationToken)
    {
        var existing = await _state.GetAsync(candidate.Manifest.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null && existing.BundleDigest != candidate.Identity.Digest)
        {
            await QuarantineAsync(candidate.StagedPath, "module-version-conflict", "Another bundle for this module is already installed.", cancellationToken).ConfigureAwait(false);
            return;
        }
        var record = existing ?? InstalledModuleRecord.Create(candidate, _timeProvider.GetUtcNow());
        if (existing is null) record = await SaveAndAuditAsync(record, "bundle.inspected", "metadata-valid", cancellationToken).ConfigureAwait(false);
        else if (record.State != ModuleLifecycleState.Verifying) record = await TransitionAsync(record, ModuleLifecycleState.Verifying, "trust-recheck", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        RetainStaged(candidate);
        await TransitionAsync(record, ModuleLifecycleState.AwaitingTrust, "publisher-untrusted", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var record in await _state.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (record.State == ModuleLifecycleState.Active && record.DesiredEnabled)
            {
                var failed = await TransitionAsync(record, ModuleLifecycleState.Failed, "runtime-restart-reconcile", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
                var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false);
                if (installed is null) continue;
                try
                {
                    var verified = await _verifier.VerifyAsync(installed.BundlePath, cancellationToken).ConfigureAwait(false);
                    var verifying = await TransitionAsync(failed, ModuleLifecycleState.Verifying, "runtime-restart-reverify", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
                    await ResolveAndActivateAsync(verifying, installed, verified, forceActivation: true, cancellationToken).ConfigureAwait(false);
                }
                catch (BundleVerificationException exception) { await TransitionAsync(failed, ModuleLifecycleState.Quarantined, exception.Code, desiredEnabled: true, cancellationToken).ConfigureAwait(false); }
            }
            else if (record.State is ModuleLifecycleState.Starting or ModuleLifecycleState.HealthChecking or ModuleLifecycleState.Draining or ModuleLifecycleState.Installing)
                await TransitionAsync(record, ModuleLifecycleState.Failed, "interrupted-transition-recovered", record.DesiredEnabled, cancellationToken).ConfigureAwait(false);
        }
    }

    private async void OnModuleExited(object? sender, ModuleExitedEventArgs args)
    {
        var gate = Gate(args.ModuleId.Value);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var record = await _state.GetAsync(args.ModuleId, CancellationToken.None).ConfigureAwait(false);
            if (record is null || record.InstanceId != args.InstanceId.Value || record.State != ModuleLifecycleState.Active) return;
            _capabilities.Unregister(args.ModuleId, args.InstanceId);
            await TransitionAsync(record, ModuleLifecycleState.Failed, $"{args.ReasonCode}:{args.ExitCode}", desiredEnabled: true, CancellationToken.None).ConfigureAwait(false);
            await ReconcileActiveDependenciesAsync(args.ModuleId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) { await _audit.AppendAsync("module.crash-handler", args.ModuleId.Value, "failure", FailureCode(exception), cancellationToken: CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<InstalledModuleRecord> TransitionAsync(InstalledModuleRecord record, ModuleLifecycleState next, string reason, bool desiredEnabled, CancellationToken cancellationToken)
    {
        if (record.State == next) return record;
        if (!AllowedTransitions.TryGetValue(record.State, out var allowed) || !allowed.Contains(next)) throw new InvalidOperationException($"Lifecycle transition {record.State} -> {next} is not allowed.");
        var updated = record with { State = next, Revision = checked(record.Revision + 1), UpdatedAt = _timeProvider.GetUtcNow(), ReasonCode = reason, DesiredEnabled = desiredEnabled, InstanceId = next is ModuleLifecycleState.Disabled or ModuleLifecycleState.Failed or ModuleLifecycleState.Quarantined ? null : record.InstanceId };
        return await SaveAndAuditAsync(updated, "module.transition", reason, cancellationToken, record.State.ToString()).ConfigureAwait(false);
    }

    private async Task<InstalledModuleRecord> SaveAndAuditAsync(InstalledModuleRecord record, string eventType, string reason, CancellationToken cancellationToken, string? prior = null)
    {
        var saved = await _state.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync(eventType, record.ModuleId.Value, "success", reason, new Dictionary<string, string?>
        {
            ["version"] = record.Version.ToString(),
            ["digest"] = record.BundleDigest,
            ["priorState"] = prior,
            ["state"] = record.State.ToString(),
            ["revision"] = record.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["instance"] = record.InstanceId
        }, cancellationToken).ConfigureAwait(false);
        return saved;
    }

    private async Task QuarantineAsync(string path, string code, string message, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(_paths.Quarantine, Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".murchalka");
        if (File.Exists(path) && IsUnder(path, _paths.Staging)) File.Move(path, destination);
        await File.WriteAllTextAsync(destination + ".reason.json", JsonSerializer.Serialize(new { code, message = message.Length <= 1024 ? message : message[..1024], at = _timeProvider.GetUtcNow() }), cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync("bundle.rejected", Path.GetFileName(path), "quarantined", code, new Dictionary<string, string?> { ["quarantine"] = Path.GetFileName(destination) }, cancellationToken).ConfigureAwait(false);
    }

    private void RetainStaged(VerifiedBundle bundle)
    {
        var destination = RetainedStagingPath(bundle.Identity.Digest);
        if (Path.GetFullPath(bundle.StagedPath) == Path.GetFullPath(destination)) return;
        if (File.Exists(destination)) File.Delete(bundle.StagedPath); else File.Move(bundle.StagedPath, destination);
    }

    private string RetainedStagingPath(string digest) => Path.Combine(_paths.Staging, digest[7..] + ".murchalka");
    private void DeleteIfStaged(string path) { if (File.Exists(path) && IsUnder(path, _paths.Staging)) File.Delete(path); }
    private static bool IsUnder(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private SemaphoreSlim Gate(string moduleId) => _moduleGates.GetOrAdd(moduleId, static _ => new SemaphoreSlim(1, 1));
    private static HashSet<ModuleLifecycleState> Set(params ModuleLifecycleState[] states) => [.. states];
    private static string FailureCode(Exception exception) => "activation-" + (exception is ModuleActivationException activation ? activation.ReasonCode : exception.GetType().Name.ToLowerInvariant());
    private static ModuleStatus ToStatus(InstalledModuleRecord value) => new(value.ModuleId.Value, value.Version.ToString(), value.BundleDigest, value.State, value.Revision, value.UpdatedAt, value.ReasonCode, value.InstanceId, value.DesiredEnabled);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_started) return;
        _supervisor.ModuleExited -= OnModuleExited;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_processing is not null) await _processing.ConfigureAwait(false);
        foreach (var capability in _capabilities.Snapshot()) _capabilities.Unregister(capability.ModuleId, capability.InstanceId);
        foreach (var record in await _state.GetAllAsync(CancellationToken.None).ConfigureAwait(false))
            if (record.InstanceId is not null) await _supervisor.StopAsync(new InstanceId(record.InstanceId), TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        await _watcher.DisposeAsync().ConfigureAwait(false);
        foreach (var gate in _moduleGates.Values) gate.Dispose();
        _moduleGates.Clear();
        _reconciliation.Dispose();
        _shutdown.Dispose();
    }
}
