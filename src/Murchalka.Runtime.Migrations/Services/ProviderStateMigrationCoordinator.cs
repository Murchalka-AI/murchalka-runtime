using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.ModuleProtocol.Json;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Dependencies;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Contracts.State;

namespace Murchalka.Runtime.Migrations.Services;

/// <summary>Applies signed migration chains through resolved storage capability providers.</summary>
public sealed class ProviderStateMigrationCoordinator : IStateMigrationCoordinator
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly RuntimePaths _paths;
    private readonly ICapabilityRegistry _capabilities;
    private readonly IRootAudit _audit;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates a provider-backed state migration coordinator.</summary>
    /// <param name="paths">The Runtime filesystem paths.</param>
    /// <param name="capabilities">The active capability registry.</param>
    /// <param name="audit">The Root audit trail.</param>
    /// <param name="timeProvider">The optional trusted time source.</param>
    public ProviderStateMigrationCoordinator(RuntimePaths paths, ICapabilityRegistry capabilities, IRootAudit audit, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paths.EnsureCreated();
    }

    /// <inheritdoc />
    public async Task ApplyPendingAsync(InstalledBundle bundle, DependencyResolutionResult resolution, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(resolution);
        if (bundle.Manifest.StorageNamespaces.Count == 0) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var declaration in bundle.Manifest.StorageNamespaces.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                var migrationSet = ReadMigrationSet(bundle, declaration);
                var dependency = FindDependency(bundle.Manifest, resolution, declaration, migrationSet.ProviderCategory);
                var ledger = await ReadLedgerAsync(bundle.Manifest.Id, declaration.Name, cancellationToken).ConfigureAwait(false);
                var pending = SelectPending(migrationSet, ledger?.Version ?? "0");
                foreach (var migration in pending)
                {
                    var artifactPath = ResolveInside(bundle.ContentPath, migration.ArtifactPath);
                    VerifyChecksum(artifactPath, migration.Checksum);
                    var payload = JsonSerializer.SerializeToElement(new
                    {
                        operation = "migrate",
                        @namespace = declaration.Name,
                        migration = new
                        {
                            id = migration.Id,
                            from = migration.FromVersion,
                            to = migration.ToVersion,
                            format = Path.GetExtension(artifactPath).TrimStart('.').ToLowerInvariant(),
                            artifact = Convert.ToBase64String(await File.ReadAllBytesAsync(artifactPath, cancellationToken).ConfigureAwait(false)),
                            checksum = migration.Checksum,
                            transactional = migration.Transactional
                        }
                    });
                    var result = await InvokeAsync(bundle, dependency, "module-state-migration", migration.Id, payload, cancellationToken).ConfigureAwait(false);
                    RequireSuccess(result, "State migration provider rejected the migration.");
                    ledger = new MigrationLedger(bundle.Manifest.Id.Value, declaration.Name, migration.ToVersion, migration.Id, _timeProvider.GetUtcNow());
                    await WriteLedgerAsync(ledger, cancellationToken).ConfigureAwait(false);
                    await _audit.AppendAsync("state.migrated", bundle.Manifest.Id.Value, "success", "storage-migration-committed", new Dictionary<string, string?>
                    {
                        ["namespace"] = declaration.Name,
                        ["migration"] = migration.Id,
                        ["from"] = migration.FromVersion,
                        ["to"] = migration.ToVersion,
                        ["provider"] = dependency.ProviderModule.Value
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<StateExport> ExportAsync(InstalledBundle bundle, DependencyResolutionResult resolution, string namespaceName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var declaration = FindNamespace(bundle, namespaceName);
        if (!declaration.Exportable) throw new InvalidOperationException("The namespace is not declared exportable.");
        var migrationSet = ReadMigrationSet(bundle, declaration);
        var dependency = FindDependency(bundle.Manifest, resolution, declaration, migrationSet.ProviderCategory);
        var ledger = await ReadLedgerAsync(bundle.Manifest.Id, declaration.Name, cancellationToken).ConfigureAwait(false);
        var result = await InvokeAsync(bundle, dependency, "module-state-export", Guid.NewGuid().ToString("N"),
            JsonSerializer.SerializeToElement(new { operation = "export", @namespace = declaration.Name, schemaVersion = ledger?.Version ?? "0" }), cancellationToken).ConfigureAwait(false);
        RequireSuccess(result, "State provider rejected export.");
        var content = Convert.FromBase64String(result.Payload?.GetProperty("artifact").GetString() ?? throw new InvalidDataException("State export response has no artifact."));
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
        var exportId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_paths.StateExports, exportId + ".state");
        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
        var export = new StateExport(exportId, bundle.Manifest.Id, declaration.Name, ledger?.Version ?? "0", path, digest, _timeProvider.GetUtcNow());
        await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(export, SerializerOptions), cancellationToken).ConfigureAwait(false);
        await _audit.AppendAsync("state.exported", bundle.Manifest.Id.Value, "success", "state-export-created", new Dictionary<string, string?>
        {
            ["namespace"] = declaration.Name,
            ["exportId"] = exportId,
            ["digest"] = digest
        }, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(content);
        return export;
    }

    /// <inheritdoc />
    public async Task ImportAsync(InstalledBundle bundle, DependencyResolutionResult resolution, StateExport stateExport, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stateExport);
        if (stateExport.ModuleId != bundle.Manifest.Id) throw new InvalidOperationException("State export belongs to another module.");
        var declaration = FindNamespace(bundle, stateExport.Namespace);
        if (!declaration.Exportable) throw new InvalidOperationException("The namespace is not declared importable.");
        var migrationSet = ReadMigrationSet(bundle, declaration);
        var dependency = FindDependency(bundle.Manifest, resolution, declaration, migrationSet.ProviderCategory);
        var content = await File.ReadAllBytesAsync(stateExport.ContentPath, cancellationToken).ConfigureAwait(false);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(digest), System.Text.Encoding.ASCII.GetBytes(stateExport.ContentDigest)))
            throw new InvalidDataException("State export digest verification failed.");
        try
        {
            var result = await InvokeAsync(bundle, dependency, "module-state-import", stateExport.ExportId,
                JsonSerializer.SerializeToElement(new { operation = "import", @namespace = declaration.Name, schemaVersion = stateExport.SchemaVersion, artifact = Convert.ToBase64String(content), digest }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result, "State provider rejected import.");
            await WriteLedgerAsync(new MigrationLedger(bundle.Manifest.Id.Value, declaration.Name, stateExport.SchemaVersion, "import:" + stateExport.ExportId, _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            await _audit.AppendAsync("state.imported", bundle.Manifest.Id.Value, "success", "state-import-committed", new Dictionary<string, string?>
            {
                ["namespace"] = declaration.Name,
                ["exportId"] = stateExport.ExportId,
                ["digest"] = digest
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(content); }
    }

    /// <inheritdoc />
    public async Task RollbackUpgradeAsync(InstalledBundle candidate, InstalledBundle prior, DependencyResolutionResult resolution, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(resolution);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var declaration in candidate.Manifest.StorageNamespaces.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                var candidateSet = ReadMigrationSet(candidate, declaration);
                var dependency = FindDependency(candidate.Manifest, resolution, declaration, candidateSet.ProviderCategory);
                var targetVersion = "0";
                var priorDeclaration = prior.Manifest.StorageNamespaces.SingleOrDefault(value => value.Name == declaration.Name);
                if (priorDeclaration is not null)
                {
                    var priorSet = ReadMigrationSet(prior, priorDeclaration);
                    targetVersion = priorSet.Migrations.Count == 0 ? "0" : priorSet.Migrations[^1].ToVersion;
                }

                var ledger = await ReadLedgerAsync(candidate.Manifest.Id, declaration.Name, cancellationToken).ConfigureAwait(false);
                var currentVersion = ledger?.Version ?? "0";
                while (!string.Equals(currentVersion, targetVersion, StringComparison.Ordinal))
                {
                    var migration = candidateSet.Migrations.SingleOrDefault(value => value.ToVersion == currentVersion)
                        ?? throw new InvalidDataException($"No rollback migration reaches schema version '{currentVersion}'.");
                    if (!migration.Reversible || migration.DownArtifactPath is null)
                        throw new InvalidOperationException($"Migration '{migration.Id}' is irreversible; automatic rollback is fail-closed.");
                    var artifactPath = ResolveInside(candidate.ContentPath, migration.DownArtifactPath);
                    var content = await File.ReadAllBytesAsync(artifactPath, cancellationToken).ConfigureAwait(false);
                    var checksum = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));
                    var operationId = "rollback-" + migration.Id;
                    var payload = JsonSerializer.SerializeToElement(new
                    {
                        operation = "migrate",
                        @namespace = declaration.Name,
                        migration = new
                        {
                            id = operationId,
                            from = migration.ToVersion,
                            to = migration.FromVersion,
                            format = Path.GetExtension(artifactPath).TrimStart('.').ToLowerInvariant(),
                            artifact = Convert.ToBase64String(content),
                            checksum,
                            transactional = migration.Transactional
                        }
                    });
                    CryptographicOperations.ZeroMemory(content);
                    var result = await InvokeAsync(candidate, dependency, "module-state-rollback", operationId, payload, cancellationToken).ConfigureAwait(false);
                    RequireSuccess(result, "State provider rejected migration rollback.");
                    currentVersion = migration.FromVersion;
                    await WriteLedgerAsync(new MigrationLedger(candidate.Manifest.Id.Value, declaration.Name, currentVersion, operationId, _timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
                    await _audit.AppendAsync("state.migration-rolled-back", candidate.Manifest.Id.Value, "success", "storage-migration-reverted", new Dictionary<string, string?>
                    {
                        ["namespace"] = declaration.Name,
                        ["migration"] = migration.Id,
                        ["from"] = migration.ToVersion,
                        ["to"] = migration.FromVersion
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<ResultEnvelope> InvokeAsync(InstalledBundle bundle, ResolvedCapabilityDependency dependency, string purpose, string idempotencyKey, JsonElement payload, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddMinutes(2);
        return await _capabilities.InvokeAsync(new InvocationEnvelope(
            Guid.NewGuid(), dependency.CapabilityId, dependency.CapabilityVersion, dependency.RuntimeInstance,
            bundle.Manifest.Id, null, new InvocationScope(null, null, null, null, null, null), purpose,
            $"binding:{bundle.Manifest.Id.Value}:{dependency.RequirementId}:{dependency.BindingRevision}",
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), null, deadline, idempotencyKey,
            "storage.operation.request@1", payload, null), cancellationToken).ConfigureAwait(false);
    }

    private static void RequireSuccess(ResultEnvelope result, string message)
    {
        if (result.Status != InvocationStatus.Succeeded)
            throw new InvalidOperationException($"{message} Code: {result.Error?.Code ?? result.Status.ToString()}.");
    }

    private static ModuleMigrationSet ReadMigrationSet(InstalledBundle bundle, StorageNamespaceDeclaration declaration)
    {
        var path = ResolveInside(bundle.ContentPath, declaration.MigrationsPath);
        var root = StructuredDocument.Load(path).AsObject();
        if (root["apiVersion"]?.GetValue<string>() != "migrations.murchalka.dev/v1" || root["kind"]?.GetValue<string>() != "ModuleMigrations")
            throw new InvalidDataException("Migration manifest kind or API version is invalid.");
        var moduleId = new ModuleId(root["module"]?.GetValue<string>() ?? throw new InvalidDataException("Migration manifest module is missing."));
        var namespaceName = root["namespace"]?.GetValue<string>() ?? throw new InvalidDataException("Migration manifest namespace is missing.");
        if (moduleId != bundle.Manifest.Id || !string.Equals(namespaceName, declaration.Name, StringComparison.Ordinal))
            throw new InvalidDataException("Migration manifest ownership does not match the signed module manifest.");
        var category = root["providerCategory"]?.GetValue<string>() ?? throw new InvalidDataException("Migration provider category is missing.");
        var directory = Path.GetDirectoryName(path)!;
        var migrations = root["versions"]?.AsArray().Select(item =>
        {
            var value = item!.AsObject();
            var artifact = value["artifact"]!.GetValue<string>();
            return new ModuleMigration(
                value["id"]!.GetValue<string>(),
                value["from"]!.GetValue<string>(),
                value["to"]!.GetValue<string>(),
                Path.GetRelativePath(bundle.ContentPath, ResolveInside(directory, artifact)).Replace(Path.DirectorySeparatorChar, '/'),
                value["checksum"]!.GetValue<string>(),
                value["transactional"]!.GetValue<bool>(),
                value["reversible"]!.GetValue<bool>(),
                value["downArtifact"]?.GetValue<string>() is { } downArtifact
                    ? Path.GetRelativePath(bundle.ContentPath, ResolveInside(directory, downArtifact)).Replace(Path.DirectorySeparatorChar, '/')
                    : null,
                value["rollbackStrategy"]?.GetValue<string>());
        }).ToArray() ?? [];
        ValidateLinearChain(migrations);
        return new ModuleMigrationSet(moduleId, namespaceName, category, migrations);
    }

    private static void ValidateLinearChain(ModuleMigration[] migrations)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < migrations.Length; index++)
        {
            var migration = migrations[index];
            if (!ids.Add(migration.Id)) throw new InvalidDataException($"Migration id '{migration.Id}' is duplicated.");
            if (migration.FromVersion == migration.ToVersion) throw new InvalidDataException($"Migration '{migration.Id}' does not advance schema version.");
            if (index > 0 && migrations[index - 1].ToVersion != migration.FromVersion)
                throw new InvalidDataException("Migration versions must form one deterministic linear chain.");
            if (migration.Reversible && string.IsNullOrWhiteSpace(migration.DownArtifactPath))
                throw new InvalidDataException($"Reversible migration '{migration.Id}' has no down artifact.");
            if (!migration.Reversible && string.IsNullOrWhiteSpace(migration.RollbackStrategy))
                throw new InvalidDataException($"Irreversible migration '{migration.Id}' has no rollback strategy.");
        }
    }

    private static ModuleMigration[] SelectPending(ModuleMigrationSet set, string currentVersion)
    {
        if (set.Migrations.Count == 0) return [];
        if (currentVersion == set.Migrations[^1].ToVersion) return [];
        var start = set.Migrations.ToList().FindIndex(value => value.FromVersion == currentVersion);
        if (start < 0) throw new InvalidDataException($"Migration chain cannot continue from stored version '{currentVersion}'.");
        return set.Migrations.Skip(start).ToArray();
    }

    private static ResolvedCapabilityDependency FindDependency(ModuleManifest manifest, DependencyResolutionResult resolution, StorageNamespaceDeclaration declaration, string providerCategory)
    {
        var requirement = manifest.CapabilityRequirements.SingleOrDefault(value => value.RequirementId == declaration.RequirementId)
            ?? throw new InvalidDataException($"Storage namespace '{declaration.Name}' references a missing required capability requirement.");
        if (!string.Equals(requirement.Category, providerCategory, StringComparison.Ordinal))
            throw new InvalidDataException("Migration provider category does not match the namespace requirement.");
        return resolution.CapabilityDependencies.SingleOrDefault(value => value.RequirementId == declaration.RequirementId)
            ?? throw new InvalidOperationException("Resolved storage dependency is unavailable.");
    }

    private static StorageNamespaceDeclaration FindNamespace(InstalledBundle bundle, string name) =>
        bundle.Manifest.StorageNamespaces.SingleOrDefault(value => value.Name == name)
        ?? throw new KeyNotFoundException($"Storage namespace '{name}' is not declared.");

    private static void VerifyChecksum(string path, string expected)
    {
        var actual = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual), System.Text.Encoding.ASCII.GetBytes(expected)))
            throw new InvalidDataException($"Migration artifact '{Path.GetFileName(path)}' checksum does not match its manifest.");
    }

    private async Task<MigrationLedger?> ReadLedgerAsync(ModuleId moduleId, string namespaceName, CancellationToken cancellationToken)
    {
        var path = LedgerPath(moduleId, namespaceName);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<MigrationLedger>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Migration ledger is empty.");
    }

    private async Task WriteLedgerAsync(MigrationLedger ledger, CancellationToken cancellationToken)
    {
        var path = LedgerPath(new ModuleId(ledger.ModuleId), ledger.Namespace);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, ledger, SerializerOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else File.Move(temporary, path);
    }

    private string LedgerPath(ModuleId moduleId, string namespaceName)
    {
        var directory = Path.Combine(_paths.MigrationState, moduleId.Value);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, namespaceName + ".json");
    }

    private static string ResolveInside(string root, string relative)
    {
        var normalizedRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(normalizedRoot, StringComparison.Ordinal)) throw new InvalidDataException("Migration path escapes bundle content.");
        if (!File.Exists(path)) throw new FileNotFoundException("Declared migration artifact is missing.", path);
        return path;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record MigrationLedger(string ModuleId, string Namespace, string Version, string LastOperation, DateTimeOffset UpdatedAt);
}
