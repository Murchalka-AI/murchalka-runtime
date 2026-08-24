namespace Murchalka.Runtime.Contracts.Secrets;

/// <summary>Describes a stored secret revision without revealing its value.</summary>
/// <param name="Name">The stable secret name.</param>
/// <param name="Revision">The monotonic revision.</param>
/// <param name="UpdatedAt">The trusted update timestamp.</param>
public sealed record SecretVersion(string Name, long Revision, DateTimeOffset UpdatedAt);
