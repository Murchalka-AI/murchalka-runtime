using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Murchalka.Runtime.Contracts.Abstractions;
using Murchalka.Runtime.Contracts.Bundles;
using Murchalka.Runtime.Contracts.Common;
using Murchalka.Runtime.Contracts.Dependencies;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Dependencies.Locks;

/// <summary>Generates deterministic, schema-compatible Runtime composition locks.</summary>
public sealed class CompositionLockStore : ICompositionLockStore
{
    private readonly RuntimePaths _paths;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a composition lock store.</summary>
    /// <param name="paths">The Runtime data paths.</param>
    /// <param name="timeProvider">The optional trusted time source.</param>
    public CompositionLockStore(RuntimePaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _paths.EnsureCreated();
    }

    /// <inheritdoc/>
    public async Task<string> WriteAsync(InstalledBundle bundle, DependencyResolutionResult resolution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.Succeeded) throw new ArgumentException("A composition lock requires a successful resolution.", nameof(resolution));
        var artifact = RuntimeArtifactSelector.SelectProcess(bundle.Manifest, RuntimeConstants.ProtocolVersion);
        var bindingRevision = resolution.CapabilityDependencies.Select(value => value.BindingRevision).DefaultIfEmpty(0).Max();
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["module"] = new JsonObject { ["id"] = bundle.Manifest.Id.Value, ["version"] = bundle.Manifest.Version.ToString(), ["bundleDigest"] = bundle.Digest },
            ["resolvedAt"] = _timeProvider.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["runtimeVersion"] = RuntimeConstants.Version.ToString(),
            ["bindingRevision"] = bindingRevision,
            ["dependencies"] = new JsonArray(resolution.CapabilityDependencies.OrderBy(value => value.RequirementId, StringComparer.Ordinal).ThenBy(value => value.ProviderModule.Value, StringComparer.Ordinal).Select(value => (JsonNode)new JsonObject
            {
                ["requirement"] = value.RequirementId,
                ["providerModule"] = value.ProviderModule.Value,
                ["providerVersion"] = value.ProviderModuleVersion.ToString(),
                ["capability"] = value.CapabilityId.Value,
                ["capabilityVersion"] = value.CapabilityVersion.ToString(),
                ["instance"] = value.LogicalInstance,
                ["bindingRevision"] = value.BindingRevision
            }).ToArray()),
            ["artifacts"] = new JsonArray(new JsonObject { ["target"] = "runtime", ["id"] = artifact.Id, ["digest"] = artifact.Digest }),
            ["contracts"] = new JsonArray(bundle.Manifest.Capabilities.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ThenBy(value => value.Version).Select(value => (JsonNode)new JsonObject
            {
                ["id"] = value.Id.Value,
                ["version"] = value.Version.ToString(),
                ["schemaDigest"] = Hash(Path.Combine(bundle.ContentPath, value.ContractPath.Replace('/', Path.DirectorySeparatorChar)))
            }).ToArray()),
            ["extensions"] = new JsonObject
            {
                ["dev.murchalka.resolver"] = new JsonObject
                {
                    ["moduleDependencies"] = new JsonArray(resolution.ModuleDependencies.OrderBy(value => value.ModuleId.Value, StringComparer.Ordinal).Select(value => (JsonNode)new JsonObject
                    {
                        ["module"] = value.ModuleId.Value,
                        ["version"] = value.Version.ToString(),
                        ["bundleDigest"] = value.BundleDigest
                    }).ToArray()),
                    ["fallbacks"] = new JsonObject(resolution.Fallbacks.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => KeyValuePair.Create<string, JsonNode?>(value.Key, value.Value)))
                }
            }
        };
        var path = Path.Combine(_paths.Locks, bundle.Manifest.Id.Value + ".lock.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    private static string Hash(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
}
