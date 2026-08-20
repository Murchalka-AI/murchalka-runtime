using System.Text.Json;
using Murchalka.Runtime.Contracts.Bindings;
using Murchalka.Runtime.Contracts.Capabilities;
using Murchalka.Runtime.Contracts.Manifests;

namespace Murchalka.Runtime.Contracts.Dependencies;

/// <summary>Contains the immutable inputs for one module dependency resolution.</summary>
/// <param name="Consumer">The consumer manifest.</param>
/// <param name="ConsumerBundleDigest">The authenticated consumer bundle digest.</param>
/// <param name="Modules">The verified module catalog.</param>
/// <param name="Providers">The healthy active capability providers.</param>
/// <param name="Bindings">The current administrative bindings.</param>
/// <param name="ScopeContext">The applicable scopes in resolution order.</param>
/// <param name="Configuration">The declarative configuration values used by conditions.</param>
public sealed record DependencyResolutionRequest(
    ModuleManifest Consumer,
    string ConsumerBundleDigest,
    IReadOnlyList<AvailableModule> Modules,
    IReadOnlyList<CapabilityProvider> Providers,
    BindingDocument Bindings,
    BindingScopeContext ScopeContext,
    IReadOnlyDictionary<string, JsonElement> Configuration);
