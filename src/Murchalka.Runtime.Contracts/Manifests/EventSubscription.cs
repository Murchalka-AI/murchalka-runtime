namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Declares an at-least-once event subscription.</summary>
/// <param name="Topic">The subscribed event topic.</param>
/// <param name="SchemaPath">The accepted payload schema path inside the immutable bundle.</param>
/// <param name="HandlerId">The module-local durable handler identifier.</param>
public sealed record EventSubscription(string Topic, string SchemaPath, string HandlerId);
