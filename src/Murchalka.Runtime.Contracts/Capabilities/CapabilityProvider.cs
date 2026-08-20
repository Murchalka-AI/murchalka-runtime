using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Capabilities;

/// <summary>Describes an active capability provider.</summary>
/// <param name="CapabilityId">The capability identifier.</param>
/// <param name="Version">The capability version.</param>
/// <param name="ModuleId">The provider module identifier.</param>
/// <param name="InstanceId">The authenticated provider instance.</param>
/// <param name="Category">The capability category.</param>
/// <param name="ContractPath">The validated contract path.</param>
/// <param name="Timeout">The declared invocation timeout.</param>
/// <param name="LogicalInstance">The stable provider instance name used by bindings.</param>
/// <param name="ModuleVersion">The provider module version.</param>
/// <param name="BundleDigest">The authenticated provider bundle digest.</param>
/// <param name="Qualifiers">The provider qualifiers.</param>
/// <param name="Scopes">The scopes supported by the provider.</param>
public sealed record CapabilityProvider(
    CapabilityId CapabilityId,
    SemanticVersion Version,
    ModuleId ModuleId,
    InstanceId InstanceId,
    string Category,
    string ContractPath,
    TimeSpan Timeout,
    string LogicalInstance,
    SemanticVersion ModuleVersion,
    string BundleDigest,
    IReadOnlyDictionary<string, JsonElement> Qualifiers,
    IReadOnlySet<BindingScopeType> Scopes);
