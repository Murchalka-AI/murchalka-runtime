namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes side-by-side upgrade and state migration requirements.</summary>
/// <param name="RollbackWindow">The minimum period for retaining the prior bundle.</param>
/// <param name="StateMigration">The required state migration behavior.</param>
public sealed record ModuleUpgradePolicy(TimeSpan RollbackWindow, StateMigrationRequirement StateMigration);
