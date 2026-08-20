using System.Text.Json;

namespace Murchalka.Runtime.Contracts.Manifests;

/// <summary>Describes a declarative configuration predicate that enables a requirement.</summary>
/// <param name="ConfigurationPath">The configuration path to inspect.</param>
/// <param name="ExpectedValue">The value that enables the requirement.</param>
public sealed record RequirementCondition(string ConfigurationPath, JsonElement ExpectedValue);
