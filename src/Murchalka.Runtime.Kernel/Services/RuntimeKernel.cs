using System.Collections.Concurrent;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Configuration;
using Murchalka.Runtime.Contracts.Dependencies;
using Murchalka.Runtime.Contracts.Events;
using Murchalka.Runtime.Contracts.Lifecycle;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.Permissions;
using Murchalka.Runtime.Contracts.Pipelines;
using Murchalka.Runtime.Contracts.Secrets;
using Murchalka.Runtime.Contracts.State;
using Murchalka.Runtime.ModuleDiscovery.Watchers;

namespace Murchalka.Runtime.Kernel.Services;

/// <summary>Coordinates secure module discovery, verification, installation, activation, recovery, and lifecycle transitions.</summary>
public sealed class RuntimeKernel : IAsyncDisposable
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

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
            [ModuleLifecycleState.HealthChecking] = Set(ModuleLifecycleState.Active, ModuleLifecycleState.Conflict, ModuleLifecycleState.Failed),
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
    private readonly IModuleConfigurationStore _configuration;
    private readonly ISecretStore _secretStore;
    private readonly ISecretBroker _secretBroker;
    private readonly IDependencyResolver _resolver;
    private readonly ICompositionLockStore _locks;
    private readonly IPipelineRuntime _pipelines;
    private readonly IEventFabric _events;
    private readonly IClientExtensionCatalog _clientExtensions;
    private readonly IStateMigrationCoordinator _migrations;
    private readonly IRootAudit _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _moduleGates = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _reconciliation = new(1, 1);
    private Task? _processing;
    private int _activeInboxOperations;
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
    /// <param name="configuration">The schema-validated module configuration store.</param>
    /// <param name="secretStore">The encrypted local secret store.</param>
    /// <param name="secretBroker">The Root secret lease broker.</param>
    /// <param name="resolver">The dependency resolver.</param>
    /// <param name="locks">The generated composition lock store.</param>
    /// <param name="pipelines">The dynamic pipeline runtime.</param>
    /// <param name="events">The durable local event fabric.</param>
    /// <param name="migrations">The provider-backed state migration coordinator.</param>
    /// <param name="clientExtensions">The verified active client extension catalog.</param>
    /// <param name="audit">The root audit trail.</param>
    /// <param name="timeProvider">The optional source of current time.</param>
    public RuntimeKernel(RuntimePaths paths, ModuleDirectoryWatcher watcher, IBundleVerifier verifier, IModuleStore store, IModuleStateStore state,
        IPermissionGrantStore grants, IModuleSupervisor supervisor, ICapabilityRegistry capabilities, IBindingStore bindings, IModuleConfigurationStore configuration,
        ISecretStore secretStore, ISecretBroker secretBroker,
        IDependencyResolver resolver, ICompositionLockStore locks, IPipelineRuntime pipelines, IEventFabric events, IStateMigrationCoordinator migrations, IClientExtensionCatalog clientExtensions,
        IRootAudit audit, TimeProvider? timeProvider = null)
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
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _secretBroker = secretBroker ?? throw new ArgumentNullException(nameof(secretBroker));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _pipelines = pipelines ?? throw new ArgumentNullException(nameof(pipelines));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
        _clientExtensions = clientExtensions ?? throw new ArgumentNullException(nameof(clientExtensions));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _supervisor.ModuleExited += OnModuleExited;
    }

    /// <summary>Gets the registry used to invoke active module capabilities.</summary>
    public ICapabilityRegistry Capabilities => _capabilities;

    /// <summary>Gets the dynamic pipeline runtime.</summary>
    public IPipelineRuntime Pipelines => _pipelines;

    /// <summary>Gets the durable local event fabric.</summary>
    public IEventFabric Events => _events;

    /// <summary>Gets the active verified client extension catalog.</summary>
    public IClientExtensionCatalog ClientExtensions => _clientExtensions;

    /// <summary>Invokes one active capability from the loopback administrative control plane.</summary>
    /// <param name="capabilityId">The exact capability identifier.</param>
    /// <param name="payload">The untrusted payload validated against the provider contract.</param>
    /// <param name="scope">The optional invocation scope.</param>
    /// <param name="idempotencyKey">The optional stable idempotency key.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The schema-validated capability result.</returns>
    public Task<ResultEnvelope> InvokeAdministrativeCapabilityAsync(
        CapabilityId capabilityId,
        JsonElement payload,
        InvocationScope? scope = null,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (idempotencyKey is { Length: > 256 } || idempotencyKey is not null && string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency key must be non-empty and at most 256 characters.", nameof(idempotencyKey));
        var providers = _capabilities.Snapshot()
            .Where(value => value.CapabilityId == capabilityId)
            .OrderByDescending(value => value.Version)
            .ThenByDescending(value => value.ModuleVersion)
            .ThenBy(value => value.ModuleId.Value, StringComparer.Ordinal)
            .ToArray();
        if (providers.Length == 0)
            throw new KeyNotFoundException($"Capability '{capabilityId}' has no active provider.");
        var selected = providers[0];
        if (providers.Skip(1).Any(value => value.Version == selected.Version && value.ModuleVersion == selected.ModuleVersion))
            throw new InvalidOperationException($"Capability '{capabilityId}' has multiple equally preferred providers.");

        var now = _timeProvider.GetUtcNow();
        var invocation = new InvocationEnvelope(
            Guid.NewGuid(),
            selected.CapabilityId,
            selected.Version,
            selected.InstanceId,
            new ModuleId("dev.murchalka.runtime-admin"),
            "root:control-api",
            scope ?? new InvocationScope(null, null, null, null, null, null),
            "runtime-administration",
            "root-control-api",
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            null,
            now.Add(selected.Timeout),
            idempotencyKey,
            $"{selected.CapabilityId.Value}.request@{selected.Version.Major}",
            payload.Clone(),
            null);
        return _capabilities.InvokeAsync(invocation, cancellationToken);
    }

    /// <summary>Starts recovery and continuous module inbox processing.</summary>
    /// <param name="cancellationToken">A token that cancels startup.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("Runtime kernel is already started.");
        _started = true;
        _paths.EnsureCreated();
        await _audit.AppendAsync("runtime.started", "runtime", "success", "zero-module-capable", new Dictionary<string, string?> { ["version"] = RuntimeConstants.Version.ToString() }, cancellationToken).ConfigureAwait(false);
        await _events.StartAsync(cancellationToken).ConfigureAwait(false);
        await RecoverAsync(cancellationToken).ConfigureAwait(false);
        _watcher.Start();
        _processing = ProcessInboxAsync(_shutdown.Token);
    }

    /// <summary>Waits until every bundle currently discovered in the module inbox has finished processing.</summary>
    /// <param name="timeout">The maximum time to wait for the inbox to become idle.</param>
    /// <param name="cancellationToken">A token that cancels the wait.</param>
    /// <returns>A task that completes when no discovery or bundle-processing work remains.</returns>
    public async Task WaitForInboxIdleAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_started) throw new InvalidOperationException("Runtime kernel must be started before waiting for the module inbox.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var deadline = _timeProvider.GetUtcNow().Add(timeout);
        var consecutiveIdleObservations = 0;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_watcher.HasPendingWork && Volatile.Read(ref _activeInboxOperations) == 0)
            {
                consecutiveIdleObservations++;
                if (consecutiveIdleObservations >= 3) return;
            }
            else
            {
                consecutiveIdleObservations = 0;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The module inbox did not become idle before the timeout elapsed.");
    }

    /// <summary>Gets the persisted status of every known module.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The module statuses ordered by module identifier.</returns>
    public async Task<IReadOnlyList<ModuleStatus>> GetStatusAsync(CancellationToken cancellationToken = default) =>
        (await _state.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(ToStatus).OrderBy(value => value.ModuleId, StringComparer.Ordinal).ToArray();

    /// <summary>Gets external protocol contributions from active verified modules.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The active protocol contributions ordered by route namespace and module.</returns>
    public async Task<IReadOnlyList<ActiveProtocolContribution>> GetProtocolContributionsAsync(CancellationToken cancellationToken = default)
    {
        var records = (await _state.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .Where(value => value.State == ModuleLifecycleState.Active && value.DesiredEnabled)
            .OrderBy(value => value.ModuleId.Value, StringComparer.Ordinal)
            .ToArray();
        var result = new List<ActiveProtocolContribution>();
        foreach (var record in records)
        {
            var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false);
            if (installed is null) continue;
            result.AddRange(installed.Manifest.ProtocolContributions.Select(contribution =>
                new ActiveProtocolContribution(installed.Manifest.Id, installed.Manifest.Version, contribution)));
        }

        return result
            .OrderBy(value => value.Contribution.RouteNamespace, StringComparer.Ordinal)
            .ThenBy(value => value.ModuleId.Value, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Gets the current validated administrative bindings.</summary>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The current binding document.</returns>
    public Task<BindingDocument> GetBindingsAsync(CancellationToken cancellationToken = default) => _bindings.GetAsync(cancellationToken);

    /// <summary>Gets the effective validated configuration for an installed module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The configuration snapshot, or <see langword="null"/> when the module is unknown.</returns>
    public async Task<ModuleConfigurationSnapshot?> GetConfigurationAsync(ModuleId moduleId, CancellationToken cancellationToken = default)
    {
        var record = await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false);
        return installed is null ? null : await _configuration.GetAsync(installed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates, commits, and applies a module configuration revision.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="values">The untrusted administrator-provided values.</param>
    /// <param name="expectedRevision">The revision observed by the administrator.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The committed snapshot, or <see langword="null"/> when the module is unknown.</returns>
    public async Task<ModuleConfigurationSnapshot?> ReplaceConfigurationAsync(ModuleId moduleId, JsonElement values, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var gate = Gate(moduleId.Value);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
            if (record is null) return null;
            var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Installed bundle is missing.");
            var policy = installed.Manifest.Configuration?.RestartPolicy
                ?? throw new InvalidOperationException("The module does not declare configuration.");
            if (policy == ConfigurationRestartPolicy.Immutable && record.State == ModuleLifecycleState.Active)
                throw new InvalidOperationException("Immutable configuration can only be initialized while the module is inactive.");
            var snapshot = await _configuration.ReplaceAsync(installed, values, expectedRevision, cancellationToken).ConfigureAwait(false);

            var outcome = "stored";
            if (record is { State: ModuleLifecycleState.Active, InstanceId: not null })
            {
                if (policy == ConfigurationRestartPolicy.Reload)
                {
                    var session = _supervisor.GetSession(new InstanceId(record.InstanceId))
                        ?? throw new InvalidOperationException("Active module session is unavailable.");
                    try
                    {
                        var update = await session.UpdateConfigurationAsync(ToProtocolSnapshot(snapshot), TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        if (!update.Succeeded)
                        {
                            await RestartUnderGateAsync(record, installed, cancellationToken).ConfigureAwait(false);
                            outcome = "reload-rejected-module-restarted";
                        }
                        else outcome = "reloaded";
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
                    {
                        await RestartUnderGateAsync(record, installed, cancellationToken).ConfigureAwait(false);
                        outcome = "reload-failed-module-restarted";
                    }
                }
                else if (policy == ConfigurationRestartPolicy.RestartModule)
                {
                    await RestartUnderGateAsync(record, installed, cancellationToken).ConfigureAwait(false);
                    outcome = "module-restarted";
                }
                else if (policy == ConfigurationRestartPolicy.RestartTarget)
                {
                    outcome = "target-restart-required";
                }
            }

            await _audit.AppendAsync("configuration.revised", moduleId.Value, "success", outcome, new Dictionary<string, string?>
            {
                ["revision"] = snapshot.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["schemaDigest"] = snapshot.SchemaDigest,
                ["restartPolicy"] = policy.ToString()
            }, cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        finally { gate.Release(); }
    }

    /// <summary>Encrypts and stores an administrator-provided secret revision.</summary>
    /// <param name="name">The stable secret name.</param>
    /// <param name="value">The secret bytes.</param>
    /// <param name="expectedRevision">The revision observed by the administrator.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>Metadata for the committed secret revision.</returns>
    public async Task<SecretVersion> PutSecretAsync(string name, ReadOnlyMemory<byte> value, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var version = await _secretStore.PutAsync(name, value, expectedRevision, cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync("secret.revised", name, "success", "encrypted-secret-stored", new Dictionary<string, string?>
        {
            ["revision"] = version.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
        return version;
    }

    /// <summary>Gets the effective permission decision for an installed module.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The effective decision, or <see langword="null"/> when the module is unknown.</returns>
    public async Task<PermissionDecision?> GetPermissionGrantAsync(ModuleId moduleId, CancellationToken cancellationToken = default)
    {
        var record = await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Installed bundle is missing.");
        var verified = await _verifier.VerifyAsync(installed.BundlePath, cancellationToken).ConfigureAwait(false);
        return await _grants.EvaluateAsync(verified, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates and atomically replaces a signed permission grant.</summary>
    /// <param name="moduleId">The module identifier.</param>
    /// <param name="document">The complete signed grant document.</param>
    /// <param name="expectedRevision">The revision observed by the administrator, or zero when no grant exists.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The effective committed decision, or <see langword="null"/> when the module is unknown.</returns>
    public async Task<PermissionDecision?> ReplacePermissionGrantAsync(
        ModuleId moduleId,
        JsonElement document,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var gate = Gate(moduleId.Value);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PermissionDecision? committed;
        var reconcile = false;
        try
        {
            var record = await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
            if (record is null) return null;
            var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Installed bundle is missing.");
            var verified = await _verifier.VerifyAsync(installed.BundlePath, cancellationToken).ConfigureAwait(false);
            var decision = await _grants.ValidateAsync(verified, document, cancellationToken).ConfigureAwait(false);
            if (!decision.Granted) throw new InvalidDataException($"Permission grant was rejected: {decision.ReasonCode}.");

            var jsonPath = Path.Combine(_paths.Grants, moduleId.Value + ".json");
            var yamlPath = Path.Combine(_paths.Grants, moduleId.Value + ".yaml");
            var currentPath = File.Exists(jsonPath) ? jsonPath : File.Exists(yamlPath) ? yamlPath : null;
            var actualRevision = currentPath is null ? 0 : File.GetLastWriteTimeUtc(currentPath).Ticks;
            if (actualRevision != expectedRevision) throw new PermissionGrantRevisionConflictException(expectedRevision, actualRevision);

            Directory.CreateDirectory(_paths.Grants);
            var temporary = jsonPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, document.GetRawText(), cancellationToken).ConfigureAwait(false);
                File.Move(temporary, jsonPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            committed = await _grants.EvaluateAsync(verified, cancellationToken).ConfigureAwait(false);
            if (!committed.Granted) throw new InvalidDataException($"Stored permission grant was rejected: {committed.ReasonCode}.");
            await _audit.AppendAsync("permission-grant.revised", moduleId.Value, "success", "signed-grant-committed", new Dictionary<string, string?>
            {
                ["grantId"] = committed.GrantId,
                ["revision"] = committed.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["expiresAt"] = committed.ExpiresAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            }, cancellationToken).ConfigureAwait(false);

            if (record.State == ModuleLifecycleState.Active)
                await RestartUnderGateAsync(record, installed, cancellationToken).ConfigureAwait(false);
            else
                reconcile = record.DesiredEnabled;
        }
        finally
        {
            gate.Release();
        }

        if (reconcile) await ReconcilePendingAsync(cancellationToken).ConfigureAwait(false);
        return committed;
    }

    /// <summary>Exports one declared module-owned storage namespace.</summary>
    /// <param name="moduleId">The owning module identifier.</param>
    /// <param name="namespaceName">The declared namespace name.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns>The authenticated state export, or <see langword="null"/> when the module is unknown.</returns>
    public async Task<StateExport?> ExportStateAsync(ModuleId moduleId, string namespaceName, CancellationToken cancellationToken = default)
    {
        var gate = Gate(moduleId.Value);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
            if (record is null) return null;
            var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Installed bundle is missing.");
            var resolution = await ResolveDependenciesAsync(installed, cancellationToken).ConfigureAwait(false);
            if (!resolution.Succeeded) throw new InvalidOperationException($"State export dependencies are unresolved: {resolution.ReasonCode}.");
            return await _migrations.ExportAsync(installed, resolution, namespaceName, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Imports one Runtime-owned authenticated state export while the consumer module is disabled.</summary>
    /// <param name="moduleId">The owning module identifier.</param>
    /// <param name="exportId">The Runtime-generated export identifier.</param>
    /// <param name="cancellationToken">A token that cancels the operation.</param>
    /// <returns><see langword="true"/> when imported; <see langword="false"/> when the module or export is unknown.</returns>
    public async Task<bool> ImportStateAsync(ModuleId moduleId, string exportId, CancellationToken cancellationToken = default)
    {
        if (exportId.Length != 32 || !exportId.All(Uri.IsHexDigit)) throw new ArgumentException("State export id is invalid.", nameof(exportId));
        var gate = Gate(moduleId.Value);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var record = await _state.GetAsync(moduleId, cancellationToken).ConfigureAwait(false);
            var metadataPath = Path.Combine(_paths.StateExports, exportId + ".state.json");
            var contentPath = Path.Combine(_paths.StateExports, exportId + ".state");
            if (record is null || !File.Exists(metadataPath) || !File.Exists(contentPath)) return false;
            if (record.State == ModuleLifecycleState.Active) throw new InvalidOperationException("State import requires the consumer module to be disabled.");
            var stateExport = JsonSerializer.Deserialize<StateExport>(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false), WebJsonOptions)
                ?? throw new InvalidDataException("State export metadata is invalid.");
            if (!string.Equals(stateExport.ExportId, exportId, StringComparison.Ordinal) || stateExport.ModuleId != moduleId || Path.GetFullPath(stateExport.ContentPath) != Path.GetFullPath(contentPath))
                throw new InvalidDataException("State export metadata identity is invalid.");
            var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Installed bundle is missing.");
            var resolution = await ResolveDependenciesAsync(installed, cancellationToken).ConfigureAwait(false);
            if (!resolution.Succeeded) throw new InvalidOperationException($"State import dependencies are unresolved: {resolution.ReasonCode}.");
            await _migrations.ImportAsync(installed, resolution, stateExport, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

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
        _pipelines.Rebuild(updated);
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
                _pipelines.UnregisterModule(record.ModuleId, instance);
                _events.UnregisterModule(record.ModuleId, instance);
                _capabilities.Unregister(record.ModuleId, instance);
                _clientExtensions.UnregisterModule(record.ModuleId);
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
            {
                Interlocked.Increment(ref _activeInboxOperations);
                try { await ProcessStagedAsync(staged, cancellationToken).ConfigureAwait(false); }
                finally { Interlocked.Decrement(ref _activeInboxOperations); }
            }
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
        await ReconcileActiveDependenciesAsync(verified.Manifest.Id, cancellationToken).ConfigureAwait(false);
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
            await UpgradeUnderGateAsync(existing, verified, cancellationToken).ConfigureAwait(false);
            return;
        }

        var record = existing ?? InstalledModuleRecord.Create(verified, _timeProvider.GetUtcNow());
        if (existing is null) record = await SaveAndAuditAsync(record, "bundle.verified", "signature-and-content-valid", cancellationToken).ConfigureAwait(false);
        else record = await TransitionAsync(record, ModuleLifecycleState.Verifying, "reverification-requested", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        var installed = await _store.InstallAsync(verified, cancellationToken).ConfigureAwait(false);
        DeleteIfStaged(verified.StagedPath);
        await ResolveAndActivateAsync(record, installed, verified, forceActivation: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpgradeUnderGateAsync(InstalledModuleRecord current, VerifiedBundle candidate, CancellationToken cancellationToken)
    {
        if (candidate.Manifest.Version.CompareTo(current.Version) <= 0)
        {
            await QuarantineAsync(candidate.StagedPath, "upgrade-version-not-newer", "A side-by-side upgrade must increase the module version.", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(candidate.Identity.Publisher, current.Publisher, StringComparison.Ordinal))
        {
            await QuarantineAsync(candidate.StagedPath, "upgrade-publisher-mismatch", "Module ownership cannot change during an ordinary upgrade.", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (candidate.Manifest.Upgrade is null)
        {
            await QuarantineAsync(candidate.StagedPath, "upgrade-policy-missing", "The candidate does not declare a side-by-side upgrade policy.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var installed = await _store.InstallAsync(candidate, cancellationToken).ConfigureAwait(false);
        DeleteIfStaged(candidate.StagedPath);
        var resolution = await ResolveDependenciesAsync(installed, cancellationToken).ConfigureAwait(false);
        if (!resolution.Succeeded)
        {
            await _audit.AppendAsync("module.upgrade", current.ModuleId.Value, "rejected", resolution.ReasonCode, new Dictionary<string, string?>
            {
                ["fromVersion"] = current.Version.ToString(),
                ["toVersion"] = candidate.Manifest.Version.ToString(),
                ["candidateDigest"] = candidate.Identity.Digest
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        var grant = await _grants.EvaluateAsync(candidate, cancellationToken).ConfigureAwait(false);
        if (!grant.Granted)
        {
            await _audit.AppendAsync("module.upgrade", current.ModuleId.Value, "rejected", grant.ReasonCode, new Dictionary<string, string?>
            {
                ["fromVersion"] = current.Version.ToString(),
                ["toVersion"] = candidate.Manifest.Version.ToString(),
                ["candidateDigest"] = candidate.Identity.Digest
            }, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _locks.WriteAsync(installed, resolution, cancellationToken).ConfigureAwait(false);

        if (current.State != ModuleLifecycleState.Active || current.InstanceId is null)
        {
            var replacement = current with
            {
                Version = candidate.Manifest.Version,
                BundleDigest = candidate.Identity.Digest,
                Publisher = candidate.Identity.Publisher,
                Revision = checked(current.Revision + 1),
                UpdatedAt = _timeProvider.GetUtcNow(),
                ReasonCode = "inactive-bundle-upgraded"
            };
            await SaveAndAuditAsync(replacement, "module.upgraded", "inactive-bundle-replaced", cancellationToken, current.State.ToString()).ConfigureAwait(false);
            if (replacement.DesiredEnabled)
            {
                var verifying = replacement.State == ModuleLifecycleState.Verifying
                    ? replacement
                    : await TransitionAsync(replacement, ModuleLifecycleState.Verifying, "upgrade-reconciliation", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
                await ResolveAndActivateAsync(verifying, installed, candidate, forceActivation: true, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        var priorBundle = await _store.OpenAsync(current.BundleDigest, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The prior installed bundle is missing.");
        var priorInstance = new InstanceId(current.InstanceId);
        IModuleGatewaySession? candidateSession = null;
        var updating = current;
        var migrationPhaseEntered = false;
        try
        {
            var configuration = await _configuration.GetAsync(installed, cancellationToken).ConfigureAwait(false);
            candidateSession = await _supervisor.StartAsync(installed, grant, ToProtocolSnapshot(configuration), CreateDependencySnapshot(installed, resolution), cancellationToken).ConfigureAwait(false);
            candidateSession.SetEventPublisher(PublishFromModuleAsync);
            candidateSession.SetSecretBroker((request, token) => _secretBroker.LeaseAsync(installed, grant, request, token));
            candidateSession.SetDependencyInvoker(_capabilities.InvokeAsync);
            var activation = await candidateSession.SendControlAsync(ControlMessageKind.Activate, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!activation.Succeeded) throw new ModuleActivationException("upgrade-activation-rejected", $"Upgrade candidate rejected activation: {activation.ErrorCode}.");
            ModuleHealth? health = null;
            for (var attempt = 0; attempt < installed.Manifest.Health.ReadinessFailureThreshold; attempt++)
            {
                health = await candidateSession.ProbeHealthAsync(installed.Manifest.Health.ReadinessTimeout, cancellationToken).ConfigureAwait(false);
                if (health.Status == ModuleHealthStatus.Ready) break;
            }
            if (health?.Status != ModuleHealthStatus.Ready) throw new ModuleActivationException("upgrade-readiness-failed", "Upgrade candidate did not pass readiness.");

            updating = await TransitionAsync(current, ModuleLifecycleState.Updating, "upgrade-candidate-ready", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
            _pipelines.UnregisterModule(current.ModuleId, priorInstance);
            _events.UnregisterModule(current.ModuleId, priorInstance);
            _capabilities.Unregister(current.ModuleId, priorInstance);
            _clientExtensions.UnregisterModule(current.ModuleId);
            await _supervisor.StopAsync(priorInstance, priorBundle.Manifest.Activation.DrainTimeout, cancellationToken).ConfigureAwait(false);

            migrationPhaseEntered = true;
            await _migrations.ApplyPendingAsync(installed, resolution, cancellationToken).ConfigureAwait(false);
            var bindings = await _bindings.GetAsync(cancellationToken).ConfigureAwait(false);
            _pipelines.RegisterModule(installed.Manifest, candidateSession.InstanceId, installed.ContentPath, bindings);
            _events.RegisterModule(installed.Manifest, candidateSession.InstanceId, installed.ContentPath, grant);
            _capabilities.Register(installed.Manifest, candidateSession.InstanceId, installed.ContentPath, installed.Digest);
            _clientExtensions.RegisterModule(installed);

            var switched = updating with
            {
                Version = candidate.Manifest.Version,
                BundleDigest = candidate.Identity.Digest,
                Publisher = candidate.Identity.Publisher,
                InstanceId = candidateSession.InstanceId.Value
            };
            var active = await TransitionAsync(switched, ModuleLifecycleState.Active, "upgrade-committed", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
            await WriteRollbackReferenceAsync(priorBundle, installed, candidate.Manifest.Upgrade.RollbackWindow, cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync("module.upgraded", active.ModuleId.Value, "success", "side-by-side-route-switched", new Dictionary<string, string?>
            {
                ["fromVersion"] = current.Version.ToString(),
                ["toVersion"] = active.Version.ToString(),
                ["priorDigest"] = current.BundleDigest,
                ["digest"] = active.BundleDigest,
                ["instance"] = active.InstanceId
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (candidateSession is not null)
            {
                _pipelines.UnregisterModule(candidate.Manifest.Id, candidateSession.InstanceId);
                _events.UnregisterModule(candidate.Manifest.Id, candidateSession.InstanceId);
                _capabilities.Unregister(candidate.Manifest.Id, candidateSession.InstanceId);
                _clientExtensions.UnregisterModule(candidate.Manifest.Id);
                await _supervisor.StopAsync(candidateSession.InstanceId, candidate.Manifest.Activation.DrainTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            if (updating.State == ModuleLifecycleState.Updating)
            {
                var failed = await TransitionAsync(updating, ModuleLifecycleState.Failed, "upgrade-failed:" + FailureCode(exception), desiredEnabled: true, CancellationToken.None).ConfigureAwait(false);
                var stateRollbackSucceeded = true;
                if (migrationPhaseEntered)
                {
                    try { await _migrations.RollbackUpgradeAsync(installed, priorBundle, resolution, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception migrationRollbackException)
                    {
                        stateRollbackSucceeded = false;
                        await _audit.AppendAsync("state.migration-rollback", current.ModuleId.Value, "failure", FailureCode(migrationRollbackException), cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    }
                }
                if (!stateRollbackSucceeded) return;
                try
                {
                    await _supervisor.StopAsync(priorInstance, priorBundle.Manifest.Activation.DrainTimeout, CancellationToken.None).ConfigureAwait(false);
                    var priorVerified = await _verifier.VerifyAsync(priorBundle.BundlePath, CancellationToken.None).ConfigureAwait(false);
                    var verifying = await TransitionAsync(failed, ModuleLifecycleState.Verifying, "upgrade-rollback", desiredEnabled: true, CancellationToken.None).ConfigureAwait(false);
                    await ResolveAndActivateAsync(verifying, priorBundle, priorVerified, forceActivation: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException)
                {
                    await _audit.AppendAsync("module.rollback", current.ModuleId.Value, "failure", FailureCode(rollbackException), cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task WriteRollbackReferenceAsync(InstalledBundle prior, InstalledBundle current, TimeSpan rollbackWindow, CancellationToken cancellationToken)
    {
        var moduleDirectory = Path.Combine(_paths.Rollback, current.Manifest.Id.Value);
        Directory.CreateDirectory(moduleDirectory);
        var path = Path.Combine(moduleDirectory, current.Manifest.Version + ".json");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var document = JsonSerializer.Serialize(new
        {
            moduleId = current.Manifest.Id.Value,
            priorVersion = prior.Manifest.Version.ToString(),
            priorDigest = prior.Digest,
            currentVersion = current.Manifest.Version.ToString(),
            currentDigest = current.Digest,
            retainUntil = _timeProvider.GetUtcNow().Add(rollbackWindow)
        });
        await File.WriteAllTextAsync(temporary, document, cancellationToken).ConfigureAwait(false);
        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else File.Move(temporary, path);
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
        await _migrations.ApplyPendingAsync(installed, resolution, cancellationToken).ConfigureAwait(false);
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
            var configuration = await _configuration.GetAsync(installed, cancellationToken).ConfigureAwait(false);
            session = await _supervisor.StartAsync(installed, grant, ToProtocolSnapshot(configuration), CreateDependencySnapshot(installed, resolution), cancellationToken).ConfigureAwait(false);
            record = await TransitionAsync(record with { InstanceId = session.InstanceId.Value }, ModuleLifecycleState.HealthChecking, "protocol-authenticated", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
            session.SetEventPublisher(PublishFromModuleAsync);
            session.SetSecretBroker((request, token) => _secretBroker.LeaseAsync(installed, grant, request, token));
            session.SetDependencyInvoker(_capabilities.InvokeAsync);
            var activation = await session.SendControlAsync(ControlMessageKind.Activate, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!activation.Succeeded) throw new ModuleActivationException("activation-control-rejected", $"Module activation failed: {activation.ErrorCode}.");
            ModuleHealth? health = null;
            for (var attempt = 0; attempt < installed.Manifest.Health.ReadinessFailureThreshold; attempt++)
            {
                health = await session.ProbeHealthAsync(installed.Manifest.Health.ReadinessTimeout, cancellationToken).ConfigureAwait(false);
                if (health.Status == ModuleHealthStatus.Ready) break;
            }
            if (health?.Status != ModuleHealthStatus.Ready) throw new ModuleActivationException("readiness-failed", "Module did not pass readiness health gate.");
            var bindings = await _bindings.GetAsync(cancellationToken).ConfigureAwait(false);
            _pipelines.RegisterModule(installed.Manifest, session.InstanceId, installed.ContentPath, bindings);
            _events.RegisterModule(installed.Manifest, session.InstanceId, installed.ContentPath, grant);
            _capabilities.Register(installed.Manifest, session.InstanceId, installed.ContentPath, installed.Digest);
            _clientExtensions.RegisterModule(installed);
            return await TransitionAsync(record, ModuleLifecycleState.Active, "health-gate-passed", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (session is not null)
            {
                _pipelines.UnregisterModule(record.ModuleId, session.InstanceId);
                _events.UnregisterModule(record.ModuleId, session.InstanceId);
                _capabilities.Unregister(record.ModuleId, session.InstanceId);
                _clientExtensions.UnregisterModule(record.ModuleId);
                await _supervisor.StopAsync(session.InstanceId, installed.Manifest.Activation.DrainTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            var state = exception is PipelineExecutionException ? ModuleLifecycleState.Conflict : ModuleLifecycleState.Failed;
            return await TransitionAsync(record, state, FailureCode(exception), desiredEnabled: true, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<DependencyResolutionResult> ResolveDependenciesAsync(InstalledBundle consumer, CancellationToken cancellationToken)
    {
        var records = await _state.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var modules = new List<AvailableModule>();
        foreach (var record in records.Where(value => value.State is not (ModuleLifecycleState.Uninstalled or ModuleLifecycleState.Quarantined)))
        {
            if (record.ModuleId == consumer.Manifest.Id && !string.Equals(record.BundleDigest, consumer.Digest, StringComparison.Ordinal)) continue;
            var installed = await _store.OpenAsync(record.BundleDigest, cancellationToken).ConfigureAwait(false);
            if (installed is not null)
                modules.Add(new AvailableModule(installed.Manifest, installed.Digest, record.State == ModuleLifecycleState.Active));
        }
        if (modules.All(value => value.BundleDigest != consumer.Digest))
            modules.Add(new AvailableModule(consumer.Manifest, consumer.Digest, false));
        var bindings = await _bindings.GetAsync(cancellationToken).ConfigureAwait(false);
        var configuration = await _configuration.GetAsync(consumer, cancellationToken).ConfigureAwait(false);
        return _resolver.Resolve(new DependencyResolutionRequest(
            consumer.Manifest,
            consumer.Digest,
            modules,
            _capabilities.Snapshot(),
            bindings,
            BindingScopeContext.ForModule(consumer.Manifest.Id),
            FlattenConfiguration(configuration.Values)));
    }

    private async Task RestartUnderGateAsync(InstalledModuleRecord record, InstalledBundle installed, CancellationToken cancellationToken)
    {
        if (record.State != ModuleLifecycleState.Active || record.InstanceId is null) return;
        var instance = new InstanceId(record.InstanceId);
        var draining = await TransitionAsync(record, ModuleLifecycleState.Draining, "configuration-restart", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        _pipelines.UnregisterModule(record.ModuleId, instance);
        _events.UnregisterModule(record.ModuleId, instance);
        _capabilities.Unregister(record.ModuleId, instance);
        _clientExtensions.UnregisterModule(record.ModuleId);
        await _supervisor.StopAsync(instance, installed.Manifest.Activation.DrainTimeout, cancellationToken).ConfigureAwait(false);
        var disabled = await TransitionAsync(draining, ModuleLifecycleState.Disabled, "configuration-restart", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        var verified = await _verifier.VerifyAsync(installed.BundlePath, cancellationToken).ConfigureAwait(false);
        var verifying = await TransitionAsync(disabled, ModuleLifecycleState.Verifying, "configuration-restart", desiredEnabled: true, cancellationToken).ConfigureAwait(false);
        var restarted = await ResolveAndActivateAsync(verifying, installed, verified, forceActivation: true, cancellationToken).ConfigureAwait(false);
        if (restarted.State != ModuleLifecycleState.Active)
            throw new InvalidOperationException($"Module did not reactivate after configuration change: {restarted.ReasonCode}.");
    }

    private static ConfigurationSnapshot ToProtocolSnapshot(ModuleConfigurationSnapshot snapshot) =>
        new(snapshot.Revision, snapshot.SchemaDigest, snapshot.Values);

    private static Dictionary<string, JsonElement> FlattenConfiguration(JsonElement values)
    {
        var flattened = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Visit(values, null, flattened);
        return flattened;

        static void Visit(JsonElement value, string? prefix, Dictionary<string, JsonElement> destination)
        {
            if (prefix is not null) destination[prefix] = value.Clone();
            if (value.ValueKind != JsonValueKind.Object) return;
            foreach (var property in value.EnumerateObject())
                Visit(property.Value, prefix is null ? property.Name : prefix + "." + property.Name, destination);
        }
    }

    private Task<EventEnvelope> PublishFromModuleAsync(EventEnvelope envelope, CancellationToken cancellationToken) =>
        _events.PublishAsync(new EventPublishRequest(
            envelope.EventId,
            envelope.Topic,
            envelope.SchemaVersion,
            envelope.ProducerModule,
            envelope.ProducerInstance,
            envelope.OccurredAt,
            envelope.TenantId,
            envelope.ActorReference,
            envelope.CorrelationId,
            envelope.CausationId,
            envelope.PartitionKey,
            envelope.DataClassification,
            envelope.Purpose,
            envelope.Payload), cancellationToken);

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
                    _pipelines.UnregisterModule(record.ModuleId, instance);
                    _events.UnregisterModule(record.ModuleId, instance);
                    _capabilities.Unregister(record.ModuleId, instance);
                    _clientExtensions.UnregisterModule(record.ModuleId);
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
            else if (record.State is ModuleLifecycleState.Starting or ModuleLifecycleState.HealthChecking or ModuleLifecycleState.Draining or ModuleLifecycleState.Installing or ModuleLifecycleState.Updating)
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
            _pipelines.UnregisterModule(args.ModuleId, args.InstanceId);
            _events.UnregisterModule(args.ModuleId, args.InstanceId);
            _capabilities.Unregister(args.ModuleId, args.InstanceId);
            _clientExtensions.UnregisterModule(args.ModuleId);
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
    private static string FailureCode(Exception exception) => "activation-" + (exception switch
    {
        ModuleActivationException activation => activation.ReasonCode,
        PipelineExecutionException pipeline => pipeline.ReasonCode,
        InvalidDataException invalid when invalid.Message.StartsWith("Expected protocol frame", StringComparison.Ordinal) => "protocol-frame-kind-mismatch",
        InvalidDataException invalid when invalid.Message.StartsWith("Control result operation id", StringComparison.Ordinal) => "control-operation-mismatch",
        InvalidDataException invalid when invalid.Message.StartsWith("Frame '", StringComparison.Ordinal) => "protocol-frame-payload-invalid",
        InvalidDataException => "protocol-invalid",
        _ => exception.GetType().Name.ToLowerInvariant()
    });
    private static ModuleStatus ToStatus(InstalledModuleRecord value) => new(value.ModuleId.Value, value.Version.ToString(), value.BundleDigest, value.State, value.Revision, value.UpdatedAt, value.ReasonCode, value.InstanceId, value.DesiredEnabled);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_started) return;
        _supervisor.ModuleExited -= OnModuleExited;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_processing is not null) await _processing.ConfigureAwait(false);
        foreach (var record in await _state.GetAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (record.InstanceId is null) continue;
            var instance = new InstanceId(record.InstanceId);
            _pipelines.UnregisterModule(record.ModuleId, instance);
            _events.UnregisterModule(record.ModuleId, instance);
            _capabilities.Unregister(record.ModuleId, instance);
            _clientExtensions.UnregisterModule(record.ModuleId);
            await _supervisor.StopAsync(instance, TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        await _watcher.DisposeAsync().ConfigureAwait(false);
        await _events.DisposeAsync().ConfigureAwait(false);
        foreach (var gate in _moduleGates.Values) gate.Dispose();
        _moduleGates.Clear();
        _reconciliation.Dispose();
        _shutdown.Dispose();
    }
}
