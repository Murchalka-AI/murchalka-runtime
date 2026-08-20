using Murchalka.ModuleProtocol.Contracts;

namespace Murchalka.Runtime.Contracts.Bindings;

/// <summary>Maps one consumer requirement and scope to an explicit provider.</summary>
/// <param name="Id">The binding identifier.</param>
/// <param name="ConsumerModule">The consuming module.</param>
/// <param name="RequirementId">The consumer-local requirement identifier.</param>
/// <param name="Scope">The exact binding scope.</param>
/// <param name="Provider">The selected provider policy.</param>
public sealed record ModuleBinding(string Id, ModuleId ConsumerModule, string RequirementId, BindingScope Scope, ProviderSelection Provider);
