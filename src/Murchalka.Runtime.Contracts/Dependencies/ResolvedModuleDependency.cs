using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Dependencies;

/// <summary>Describes one resolved exact-module dependency.</summary>
/// <param name="ModuleId">The provider module identifier.</param>
/// <param name="Version">The resolved provider module version.</param>
/// <param name="BundleDigest">The provider bundle digest.</param>
public sealed record ResolvedModuleDependency(ModuleId ModuleId, SemanticVersion Version, string BundleDigest);
