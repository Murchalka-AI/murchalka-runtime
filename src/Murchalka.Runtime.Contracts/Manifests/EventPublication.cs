namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Declares an event topic and payload schema a module may publish.</summary>
/// <param name="Topic">The event topic.</param>
/// <param name="SchemaPath">The payload schema path inside the immutable bundle.</param>
public sealed record EventPublication(string Topic, string SchemaPath);
