using System.Text.Json;
using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Contains the Runtime-relevant projection of a validated module manifest.</summary>
/// <param name="Id">The module identifier.</param>
/// <param name="Name">The display name.</param>
/// <param name="Version">The module version.</param>
/// <param name="Publisher">The publisher identifier.</param>
/// <param name="RuntimeCompatibility">The supported Runtime version range.</param>
/// <param name="ProtocolVersion">The required Module Protocol major.</param>
/// <param name="RuntimeArtifacts">The declared runtime artifacts.</param>
/// <param name="Capabilities">The declared capabilities.</param>
/// <param name="ModuleRequirements">The required exact-module dependencies.</param>
/// <param name="CapabilityRequirements">The required capability or category dependencies.</param>
/// <param name="OptionalCapabilityRequirements">The optional capability or category dependencies.</param>
/// <param name="ConflictingModules">The module conflicts declared by this module.</param>
/// <param name="PipelineDefinitionPaths">The explicitly declared pipeline definition documents.</param>
/// <param name="PipelineContributions">The pipeline handlers contributed by the module.</param>
/// <param name="EventPublications">The events the module is allowed to publish.</param>
/// <param name="EventSubscriptions">The event handlers contributed by the module.</param>
/// <param name="Configuration">The optional module configuration declaration.</param>
/// <param name="StorageNamespaces">The module-owned storage namespaces.</param>
/// <param name="RequestedPermissions">The manifest permission request.</param>
/// <param name="Health">The health policy.</param>
/// <param name="Activation">The activation policy.</param>
/// <param name="Upgrade">The optional side-by-side upgrade policy.</param>
/// <param name="Document">The complete validated manifest document.</param>
public sealed record ModuleManifest(
    ModuleId Id,
    string Name,
    SemanticVersion Version,
    string Publisher,
    string RuntimeCompatibility,
    int ProtocolVersion,
    IReadOnlyList<RuntimeArtifact> RuntimeArtifacts,
    IReadOnlyList<ProvidedCapability> Capabilities,
    IReadOnlyList<ModuleRequirement> ModuleRequirements,
    IReadOnlyList<CapabilityRequirement> CapabilityRequirements,
    IReadOnlyList<CapabilityRequirement> OptionalCapabilityRequirements,
    IReadOnlyList<ModuleRequirement> ConflictingModules,
    IReadOnlyList<string> PipelineDefinitionPaths,
    IReadOnlyList<PipelineContribution> PipelineContributions,
    IReadOnlyList<EventPublication> EventPublications,
    IReadOnlyList<EventSubscription> EventSubscriptions,
    ConfigurationDeclaration? Configuration,
    IReadOnlyList<StorageNamespaceDeclaration> StorageNamespaces,
    JsonElement RequestedPermissions,
    HealthPolicy Health,
    ActivationPolicy Activation,
    ModuleUpgradePolicy? Upgrade,
    JsonElement Document)
{
    /// <summary>Gets the signed client artifacts supplied by this module.</summary>
    public IReadOnlyList<ClientArtifact> ClientArtifacts { get; init; } = [];

    /// <summary>Gets the versioned UI component contributions supplied by this module.</summary>
    public IReadOnlyList<UiComponentContribution> UiComponents { get; init; } = [];

    /// <summary>Gets the bounded external protocol routes supplied by this module.</summary>
    public IReadOnlyList<ProtocolContribution> ProtocolContributions { get; init; } = [];

    /// <summary>Gets the stable module id and version key.</summary>
    public string Key => $"{Id.Value}@{Version}";

    /// <summary>Gets whether the manifest declares any required dependency.</summary>
    public bool HasRequiredDependencies => ModuleRequirements.Count > 0 || CapabilityRequirements.Count > 0;
}
