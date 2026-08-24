using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Dependencies;
using Murchalka.Runtime.Contracts.Manifests;
using Murchalka.Runtime.Migrations.Services;
using Murchalka.Runtime.Tests.Infrastructure;

namespace Murchalka.Runtime.Tests.Persistence;

/// <summary>Verifies authenticated provider-backed state migration coordination.</summary>
public sealed class StateMigrationCoordinatorTests
{
    /// <summary>Verifies that one signed migration is routed and durably deduplicated.</summary>
    [Fact]
    public async Task SignedLinearMigrationIsAppliedOnceThroughResolvedProvider()
    {
        using var directory = new TestDirectory();
        var content = Path.Combine(directory.Path, "content");
        var migrations = Path.Combine(content, "migrations");
        Directory.CreateDirectory(migrations);
        var script = Encoding.UTF8.GetBytes("CREATE TABLE sample(id TEXT PRIMARY KEY);");
        var checksum = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(script));
        await File.WriteAllBytesAsync(Path.Combine(migrations, "001.sql"), script);
        await File.WriteAllTextAsync(Path.Combine(migrations, "migrations.yaml"), $$"""
        apiVersion: migrations.murchalka.dev/v1
        kind: ModuleMigrations
        module: dev.murchalka.stateful
        namespace: records
        providerCategory: storage.records
        versions:
          - id: 001-initial
            from: "0"
            to: "1"
            artifact: 001.sql
            checksum: {{checksum}}
            transactional: true
            reversible: false
            rollbackStrategy: applicationForwardFix
        """);
        var requirement = new CapabilityRequirement("records", null, "storage.records", VersionRangeExpression.Parse(">=1.0.0 <2.0.0"),
            new Dictionary<string, JsonElement>(), RequirementCardinality.ExactlyOne, RequirementSelectionMode.Admin, null, null, null, false);
        var manifest = Phase3TestModuleFactory.Create("dev.murchalka.stateful") with
        {
            CapabilityRequirements = [requirement],
            StorageNamespaces = [new StorageNamespaceDeclaration("records", "records", "migrations/migrations.yaml", DataClassification.Internal, true, StoragePurgeMode.Retain)]
        };
        var bundle = new InstalledBundle("sha256:" + new string('c', 64), string.Empty, content, manifest, DateTimeOffset.UtcNow);
        var dependency = new ResolvedCapabilityDependency("records", new ModuleId("dev.murchalka.storage-sqlite"), new SemanticVersion(1, 0, 0),
            "sha256:" + new string('d', 64), new CapabilityId("storage.sqlite.records"), new SemanticVersion(1, 0, 0), "default", new InstanceId("storage-instance"), 4);
        var resolution = new DependencyResolutionResult(DependencyResolutionState.Resolved, "resolved", [], [dependency], new Dictionary<string, string>(), [], []);
        var capabilities = new CapturingCapabilityRegistry();
        using var coordinator = new ProviderStateMigrationCoordinator(new RuntimePaths(Path.Combine(directory.Path, "runtime")), capabilities, new NoopRootAudit());

        await coordinator.ApplyPendingAsync(bundle, resolution, CancellationToken.None);
        await coordinator.ApplyPendingAsync(bundle, resolution, CancellationToken.None);

        var invocation = Assert.Single(capabilities.Invocations);
        Assert.Equal(manifest.Id, invocation.ConsumerModuleId);
        Assert.Equal("001-initial", invocation.IdempotencyKey);
        Assert.Equal(checksum, invocation.Payload!.Value.GetProperty("migration").GetProperty("checksum").GetString());
    }

    private sealed class CapturingCapabilityRegistry : ICapabilityRegistry
    {
        public List<InvocationEnvelope> Invocations { get; } = [];
        public void Register(ModuleManifest manifest, InstanceId instanceId, string contentPath, string bundleDigest) => throw new NotSupportedException();
        public void Unregister(ModuleId moduleId, InstanceId instanceId) => throw new NotSupportedException();
        public IReadOnlyList<CapabilityProvider> Snapshot() => [];
        public Task<ResultEnvelope> InvokeAsync(InvocationEnvelope invocation, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            return Task.FromResult(new ResultEnvelope(invocation.InvocationId, InvocationStatus.Succeeded, JsonSerializer.SerializeToElement(new { version = "1" }), null, null, [], [], invocation.IdempotencyKey));
        }
    }
}
